#!/usr/bin/env bash
set -euo pipefail

api_url="${E2E_API_URL:-http://127.0.0.1:8080}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
run_suffix="$(date +%s)-$$"

assert_json() {
  local filter="$1"
  local payload="$2"
  local description="$3"
  if ! jq -e "${filter}" <<<"${payload}" >/dev/null; then
    echo "Assertion failed: ${description}" >&2
    jq . <<<"${payload}" >&2
    exit 1
  fi
}

json_post() {
  local url="$1"
  local token="$2"
  local body="$3"
  curl --fail --silent --show-error \
    -H "Authorization: Bearer ${token}" \
    -H "Content-Type: application/json" \
    -X POST "${url}" \
    --data "${body}"
}

development_token() {
  local tenant="$1"
  local user="$2"
  curl --fail --silent --show-error \
    -H "Content-Type: application/json" \
    -X POST "${api_url}/api/v1/auth/development-token" \
    --data "{\"tenantId\":\"${tenant}\",\"userId\":\"${user}\",\"permissions\":[\"knowledge.read\",\"documents.write\",\"tickets.write\",\"approvals.approve\"],\"accessTags\":[\"employee\"]}" \
    | jq -r '.accessToken'
}

echo "Waiting for the full stack..."
for _ in $(seq 1 90); do
  if curl --fail --silent "${api_url}/health/ready" >/dev/null; then
    break
  fi
  sleep 2
done
curl --fail --silent --show-error "${api_url}/health/ready" >/dev/null

primary_token="$(development_token "e2e-tenant-${run_suffix}" "e2e-user")"
other_token="$(development_token "other-tenant-${run_suffix}" "other-user")"

echo "Uploading and indexing a tenant-scoped document..."
upload_response="$(curl --fail --silent --show-error \
  -H "Authorization: Bearer ${primary_token}" \
  -F "accessTags=employee" \
  -F "sourceUri=demo://e2e/${run_suffix}/launch-code" \
  -F "file=@${repo_root}/samples/e2e/launch-code.txt;type=text/plain" \
  "${api_url}/api/v1/documents")"
ingestion_id="$(jq -r '.ingestionId' <<<"${upload_response}")"

for _ in $(seq 1 45); do
  ingestion_response="$(curl --fail --silent --show-error \
    -H "Authorization: Bearer ${primary_token}" \
    "${api_url}/api/v1/documents/ingestions/${ingestion_id}")"
  ingestion_status="$(jq -r '.status' <<<"${ingestion_response}")"
  if [[ "${ingestion_status}" == "Completed" ]]; then
    break
  fi
  if [[ "${ingestion_status}" == "Failed" ]]; then
    jq . <<<"${ingestion_response}"
    exit 1
  fi
  sleep 1
done
[[ "${ingestion_status}" == "Completed" ]]
[[ "$(jq -r '.chunkCount' <<<"${ingestion_response}")" -ge 1 ]]

echo "Asking a grounded question against the uploaded document..."
conversation="$(json_post "${api_url}/api/v1/conversations" "${primary_token}" '{"title":"Full-stack E2E"}')"
conversation_id="$(jq -r '.id' <<<"${conversation}")"
answer="$(curl --fail --silent --show-error \
  -H "Authorization: Bearer ${primary_token}" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: e2e-grounded-${run_suffix}" \
  -X POST "${api_url}/api/v1/conversations/${conversation_id}/messages" \
  --data '{"message":"What is the synthetic launch code? The synthetic launch code is ORBIT-42."}')"
assert_json '.status == "Completed" and .isGrounded == true and (.citations | length) > 0' "${answer}" \
  "the uploaded document must produce a grounded answer with citations"
assert_json '.answer | contains("ORBIT-42")' "${answer}" \
  "the grounded answer must contain the uploaded fact"

echo "Verifying document isolation from another tenant..."
other_conversation="$(json_post "${api_url}/api/v1/conversations" "${other_token}" '{"title":"Isolation E2E"}')"
other_conversation_id="$(jq -r '.id' <<<"${other_conversation}")"
other_answer="$(curl --fail --silent --show-error \
  -H "Authorization: Bearer ${other_token}" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: e2e-isolation-${run_suffix}" \
  -X POST "${api_url}/api/v1/conversations/${other_conversation_id}/messages" \
  --data '{"message":"What is the synthetic launch code? The synthetic launch code is ORBIT-42."}')"
assert_json '.isNoAnswer == true and (.citations | length) == 0' "${other_answer}" \
  "another tenant must not retrieve the uploaded document"

echo "Verifying human approval and single-use execution..."
proposal="$(curl --fail --silent --show-error \
  -H "Authorization: Bearer ${primary_token}" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: e2e-ticket-${run_suffix}" \
  -X POST "${api_url}/api/v1/conversations/${conversation_id}/messages" \
  --data '{"message":"Create a support ticket for a broken demo laptop."}')"
assert_json '.status == "PendingApproval" and .approval.toolName == "ticket-create"' "${proposal}" \
  "a state-changing tool must wait for human approval"
approval_id="$(jq -r '.approval.approvalId' <<<"${proposal}")"
approval_token="$(jq -r '.approval.approvalToken' <<<"${proposal}")"
approved="$(json_post \
  "${api_url}/api/v1/approvals/${approval_id}/approve" \
  "${primary_token}" \
  "{\"approvalToken\":\"${approval_token}\"}")"
assert_json '.approval.status == "Executed" and .run.status == "Completed"' "${approved}" \
  "approval must execute the pending action"
assert_json '.run.answer | contains("TKT-")' "${approved}" \
  "the approved mock ticket must return its identifier"

replay_status="$(curl --silent --output /dev/null --write-out '%{http_code}' \
  -H "Authorization: Bearer ${primary_token}" \
  -H "Content-Type: application/json" \
  -X POST "${api_url}/api/v1/approvals/${approval_id}/approve" \
  --data "{\"approvalToken\":\"${approval_token}\"}")"
if [[ "${replay_status}" != "409" ]]; then
  echo "Assertion failed: approval replay returned HTTP ${replay_status}, expected 409" >&2
  exit 1
fi

echo "Full-stack E2E passed: upload, ingestion, grounded answer, tenant isolation, and approval."
