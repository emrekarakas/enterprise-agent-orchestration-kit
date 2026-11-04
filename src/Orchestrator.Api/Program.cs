using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Orchestrator.AgentFramework;
using Orchestrator.Api;
using Orchestrator.Api.Swagger;
using Orchestrator.Application;
using Orchestrator.Infrastructure;
using Orchestrator.Observability;
using Orchestrator.Providers;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddAgentAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Enterprise Agent Orchestration Kit API",
        Version = "v1",
        Description = "A deterministic local vertical slice for tenant-isolated, grounded agent execution. "
            + "Local tokens and demo documents are synthetic; deployed environments require OIDC mode."
    });
    options.CustomSchemaIds(type => type.FullName?.Replace('+', '.') ?? type.Name);
    options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, "Orchestrator.Api.xml"));
    options.OperationFilter<DemoIdentityHeadersOperationFilter>();
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "OIDC access token. In local Development, Swagger obtains an ephemeral demo token automatically."
    });
});
var healthChecks = builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst(AgentClaimTypes.TenantId)?.Value
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 26_214_400);
builder.Services.AddAgentKitObservability("orchestrator-api");

var persistenceProvider = builder.Configuration["Persistence:Provider"] ?? "InMemory";
if (string.Equals(persistenceProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connectionString = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required for PostgreSQL persistence.");
    builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connectionString));
    builder.Services.AddScoped<PostgresPlatformStore>();
    builder.Services.AddScoped<IConversationStore>(services => services.GetRequiredService<PostgresPlatformStore>());
    builder.Services.AddScoped<IRunStore>(services => services.GetRequiredService<PostgresPlatformStore>());
    builder.Services.AddScoped<IApprovalStore>(services => services.GetRequiredService<PostgresPlatformStore>());
    builder.Services.AddScoped<IFeedbackStore>(services => services.GetRequiredService<PostgresPlatformStore>());
    builder.Services.AddScoped<IAuditLog>(services => services.GetRequiredService<PostgresPlatformStore>());
    builder.Services.AddSingleton<InMemoryPlatformStores>();
    healthChecks.AddCheck<PostgresReadinessHealthCheck>("postgres", tags: ["ready"]);
}
else
{
    builder.Services.AddSingleton<InMemoryPlatformStores>();
    builder.Services.AddSingleton<IConversationStore>(services => services.GetRequiredService<InMemoryPlatformStores>());
    builder.Services.AddSingleton<IRunStore>(services => services.GetRequiredService<InMemoryPlatformStores>());
    builder.Services.AddSingleton<IApprovalStore>(services => services.GetRequiredService<InMemoryPlatformStores>());
    builder.Services.AddSingleton<IFeedbackStore>(services => services.GetRequiredService<InMemoryPlatformStores>());
    builder.Services.AddSingleton<IAuditLog>(services => services.GetRequiredService<InMemoryPlatformStores>());
}

var cacheProvider = builder.Configuration["Cache:Provider"] ?? "InMemory";
if (string.Equals(cacheProvider, "Redis", StringComparison.OrdinalIgnoreCase))
{
    var redisSection = builder.Configuration.GetSection(RedisOptions.SectionName);
    builder.Services.Configure<RedisOptions>(redisSection);
    var redisOptions = redisSection.Get<RedisOptions>() ?? new RedisOptions();
    var redisConfiguration = ConfigurationOptions.Parse(redisOptions.ConnectionString);
    redisConfiguration.AbortOnConnectFail = false;
    redisConfiguration.ClientName = "enterprise-agent-orchestrator";
    builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect(redisConfiguration));
    builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
    healthChecks.AddCheck<RedisReadinessHealthCheck>("redis", tags: ["ready"]);
}
else if (string.Equals(cacheProvider, "Postgres", StringComparison.OrdinalIgnoreCase)
    && string.Equals(persistenceProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<IIdempotencyStore>(services => services.GetRequiredService<PostgresPlatformStore>());
}
else if (string.Equals(cacheProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IIdempotencyStore>(services => services.GetRequiredService<InMemoryPlatformStores>());
}
else
{
    throw new InvalidOperationException("Cache:Provider must be InMemory, Postgres, or Redis.");
}

var messagingProvider = builder.Configuration["Messaging:Provider"] ?? "InMemory";
if (string.Equals(messagingProvider, "AzureServiceBus", StringComparison.OrdinalIgnoreCase))
{
    var serviceBusSection = builder.Configuration.GetSection(AzureServiceBusOptions.SectionName);
    builder.Services.Configure<AzureServiceBusOptions>(serviceBusSection);
    var serviceBusOptions = serviceBusSection.Get<AzureServiceBusOptions>() ?? new AzureServiceBusOptions();
    builder.Services.AddSingleton(AzureServiceBusClientFactory.Create(serviceBusOptions));
    builder.Services.AddSingleton<IRunEventBroker>(services => new AzureServiceBusRunEventBroker(
        services.GetRequiredService<InMemoryPlatformStores>(),
        services.GetRequiredService<Azure.Messaging.ServiceBus.ServiceBusClient>(),
        services.GetRequiredService<IOptions<AzureServiceBusOptions>>()));
}
else if (string.Equals(messagingProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IRunEventBroker>(services => services.GetRequiredService<InMemoryPlatformStores>());
}
else
{
    throw new InvalidOperationException("Messaging:Provider must be InMemory or AzureServiceBus.");
}

var blobSection = builder.Configuration.GetSection(AzureBlobStorageOptions.SectionName);
builder.Services.Configure<AzureBlobStorageOptions>(blobSection);
var blobOptions = blobSection.Get<AzureBlobStorageOptions>() ?? new AzureBlobStorageOptions();
if (blobOptions.Enabled)
{
    builder.Services.AddSingleton(AzureBlobContainerClientFactory.Create(blobOptions));
    builder.Services.AddSingleton<IDocumentSource, AzureBlobDocumentSource>();
}

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection(RetrievalOptions.SectionName));
builder.Services.AddSingleton<DeterministicFakeAiProvider>();
builder.Services.AddSingleton<IRerankingProvider>(services => services.GetRequiredService<DeterministicFakeAiProvider>());
builder.Services.AddSingleton<ISafetyProvider>(services => services.GetRequiredService<DeterministicFakeAiProvider>());
builder.Services.AddSingleton<ITokenCounter>(services => services.GetRequiredService<DeterministicFakeAiProvider>());

var aiProvider = builder.Configuration["AI:Provider"] ?? "DeterministicFake";
if (string.Equals(aiProvider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase))
{
    var azureOpenAiSection = builder.Configuration.GetSection(AzureOpenAiOptions.SectionName);
    builder.Services.Configure<AzureOpenAiOptions>(azureOpenAiSection);
    var azureOpenAiOptions = azureOpenAiSection.Get<AzureOpenAiOptions>() ?? new AzureOpenAiOptions();
    builder.Services.AddSingleton(AzureOpenAiClientFactory.Create(azureOpenAiOptions));
    builder.Services.AddSingleton<AzureOpenAiProvider>();
    builder.Services.AddSingleton<IChatCompletionProvider>(services => services.GetRequiredService<AzureOpenAiProvider>());
    builder.Services.AddSingleton<IStructuredCompletionProvider>(services => services.GetRequiredService<AzureOpenAiProvider>());
    builder.Services.AddSingleton<IEmbeddingProvider>(services => services.GetRequiredService<AzureOpenAiProvider>());
}
else if (string.Equals(aiProvider, "DeterministicFake", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IChatCompletionProvider>(services => services.GetRequiredService<DeterministicFakeAiProvider>());
    builder.Services.AddSingleton<IStructuredCompletionProvider>(services => services.GetRequiredService<DeterministicFakeAiProvider>());
    builder.Services.AddSingleton<IEmbeddingProvider>(services => services.GetRequiredService<DeterministicFakeAiProvider>());
}
else if (string.Equals(aiProvider, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase))
{
    var compatibleSection = builder.Configuration.GetSection(OpenAiCompatibleOptions.SectionName);
    builder.Services.Configure<OpenAiCompatibleOptions>(compatibleSection);
    builder.Services.AddHttpClient<OpenAiCompatibleProvider>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;
        client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }).AddStandardResilienceHandler();
    builder.Services.AddScoped<IChatCompletionProvider>(services => services.GetRequiredService<OpenAiCompatibleProvider>());
    builder.Services.AddScoped<IStructuredCompletionProvider>(services => services.GetRequiredService<OpenAiCompatibleProvider>());
    builder.Services.AddScoped<IEmbeddingProvider>(services => services.GetRequiredService<OpenAiCompatibleProvider>());
}
else
{
    throw new InvalidOperationException("AI:Provider must be DeterministicFake, OpenAiCompatible, or AzureOpenAI.");
}

var documentWorkerSection = builder.Configuration.GetSection(DocumentWorkerOptions.SectionName);
builder.Services.Configure<DocumentWorkerOptions>(documentWorkerSection);
var documentWorkerEnabled = documentWorkerSection.GetValue<bool>(nameof(DocumentWorkerOptions.Enabled));
if (documentWorkerEnabled)
{
    builder.Services.AddHttpClient<DocumentWorkerClient>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<DocumentWorkerOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }).AddStandardResilienceHandler();
    builder.Services.AddScoped<IDocumentIngestionGateway>(services =>
        services.GetRequiredService<DocumentWorkerClient>());
    builder.Services.AddHttpClient("document-worker-readiness", (services, client) =>
    {
        var options = services.GetRequiredService<IOptions<DocumentWorkerOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(Math.Min(options.TimeoutSeconds, 5));
    });
    healthChecks.AddCheck<DocumentWorkerReadinessHealthCheck>("document-worker", tags: ["ready"]);
}
else
{
    builder.Services.AddSingleton<IDocumentIngestionGateway, DisabledDocumentIngestionGateway>();
}

var configuredSearchProvider = builder.Configuration["Search:Provider"];
var searchProvider = string.IsNullOrWhiteSpace(configuredSearchProvider)
    ? (documentWorkerEnabled ? "DocumentWorker" : "InMemory")
    : configuredSearchProvider;
if (string.Equals(searchProvider, "AzureAiSearch", StringComparison.OrdinalIgnoreCase))
{
    var azureSearchSection = builder.Configuration.GetSection(AzureAiSearchOptions.SectionName);
    builder.Services.Configure<AzureAiSearchOptions>(azureSearchSection);
    var azureSearchOptions = azureSearchSection.Get<AzureAiSearchOptions>() ?? new AzureAiSearchOptions();
    if (string.IsNullOrWhiteSpace(azureSearchOptions.ApiKey))
    {
        builder.Services.AddSingleton(AzureAiSearchCredentialFactory.Create(azureSearchOptions));
    }

    builder.Services.AddHttpClient<AzureAiSearchIndex>((services, client) =>
    {
        var options = services.GetRequiredService<IOptions<AzureAiSearchOptions>>().Value;
        client.BaseAddress = new Uri(options.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(10);
    }).AddStandardResilienceHandler();
    builder.Services.AddScoped<ISearchIndex>(services => services.GetRequiredService<AzureAiSearchIndex>());
    healthChecks.AddCheck<AzureAiSearchReadinessHealthCheck>("azure-ai-search", tags: ["ready"]);
}
else if (string.Equals(searchProvider, "DocumentWorker", StringComparison.OrdinalIgnoreCase) && documentWorkerEnabled)
{
    builder.Services.AddScoped<ISearchIndex>(services => services.GetRequiredService<DocumentWorkerClient>());
}
else if (string.Equals(searchProvider, "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ISearchIndex, InMemoryEnterpriseSearch>();
}
else
{
    throw new InvalidOperationException("Search:Provider must be InMemory, DocumentWorker, or AzureAiSearch.");
}

builder.Services.AddScoped<IAgentTool, EnterpriseSearchTool>();
builder.Services.AddScoped<IAgentTool, TicketCreationMockTool>();
builder.Services.AddScoped<ToolRegistry>();
builder.Services.AddScoped<ToolDispatcher>();
builder.Services.AddScoped<IAgentDefinition, KnowledgeAgent>();
builder.Services.AddScoped<IAgentDefinition, DocumentAnalystAgent>();
builder.Services.AddScoped<IAgentDefinition, WorkflowAgent>();
builder.Services.AddScoped<AgentRegistry>();
builder.Services.AddScoped<IAgentRuntime, MafAgentRuntime>();

builder.Services.AddScoped<IRequestPreparationStage, NormalizeInputStage>();
builder.Services.AddScoped<IRequestPreparationStage, InputSafetyStage>();
builder.Services.AddScoped<IRequestPreparationStage, SecretRedactionStage>();
builder.Services.AddScoped<IRequestPreparationStage>(services => new ConversationContextStage(
    services.GetRequiredService<IConversationStore>(),
    builder.Configuration.GetValue("ConversationMemory:FetchLimit", 50),
    builder.Configuration.GetValue("ConversationMemory:MaxMessages", 12),
    builder.Configuration.GetValue("ConversationMemory:MaxCharacters", 12_000)));
builder.Services.AddScoped<IRequestPreparationStage, IntentClassificationStage>();
builder.Services.AddScoped<IRequestPreparationStage, PolicyEvaluationStage>();
builder.Services.AddScoped<IPolicyEvaluator, DefaultPolicyEvaluator>();
builder.Services.AddScoped<RequestPreparationPipeline>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<AgentRunService>();
builder.Services.AddScoped<ApprovalService>();
builder.Services.AddScoped<FeedbackService>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Enterprise Agent Orchestration Kit API v1");
        options.RoutePrefix = "swagger";
        options.DocumentTitle = "Enterprise Agent Orchestration Kit API";
        options.DisplayRequestDuration();
        options.EnableTryItOutByDefault();
        options.UseRequestInterceptor("""
            async (request) => {
              request.headers = request.headers || {};
              const isApi = request.url.includes('/api/v1/');
              const isTokenRequest = request.url.includes('/api/v1/auth/development-token');
              if (isApi && !isTokenRequest && !request.headers['Authorization'] && !request.headers['authorization']) {
                try {
                  const cached = sessionStorage.getItem('agent-kit-development-token');
                  let token = cached;
                  if (!token) {
                    const tokenResponse = await fetch('/api/v1/auth/development-token', {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json' },
                      body: JSON.stringify({})
                    });
                    if (tokenResponse.ok) {
                      const payload = await tokenResponse.json();
                      token = payload.accessToken;
                      sessionStorage.setItem('agent-kit-development-token', token);
                    }
                  }
                  if (token) request.headers['Authorization'] = `Bearer ${token}`;
                } catch (_) {
                  // The normal 401 response remains visible when automatic local authentication is unavailable.
                }
              }
              const existing = request.headers['Idempotency-Key'] || request.headers['idempotency-key'];
              if (!existing && request.method?.toUpperCase() === 'POST' && request.url.includes('/messages')) {
                const suffix = globalThis.crypto?.randomUUID?.() || `${Date.now()}-${Math.random().toString(16).slice(2)}`;
                request.headers['Idempotency-Key'] = `swagger-${suffix}`;
              }
              return request;
            }
            """);
    });
}

if (string.Equals(persistenceProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    await dbContext.Database.MigrateAsync().ConfigureAwait(false);
}

app.MapControllers();

app.Run();

public partial class Program;
