using NewHeap.Platform.Common;

var builder = DistributedApplication.CreateBuilder(args);
builder.Configuration.ConfigureNhCommonConfiguration(args);

var postgres = builder.AddPostgres("postgres");
var database = postgres.AddDatabase("sample-project-management");

var rabbitMqUserName = builder.AddParameter("rabbitmq-username");
var rabbitMqPassword = builder.AddParameter("rabbitmq-password", secret: true);

var rabbitMq = builder
    .AddRabbitMQ("rabbitmq", rabbitMqUserName, rabbitMqPassword)
    .WithManagementPlugin()
    .WithDataVolume("sample-project-management-rabbitmq");

var api = builder
    .AddProject<Projects.SampleProjectManagement_Api>("sample-project-management-api")
    .WithReference(database)
    .WithReference(rabbitMq)
    .WaitFor(database)
    .WaitFor(rabbitMq)
    .WithUrlForEndpoint("https", endpoint => endpoint.Url = "/scalar");

api.WithEnvironment(
    "ApiClients__SampleProjectManagement__BaseAddress",
    api.GetEndpoint("https"));

var management = builder
    .AddJavaScriptApp(
        "sample-project-management-management",
        "../../../Front-end",
        runScriptName: "start:management")
    .WithReference(api)
    .WithHttpEndpoint(port: 4210, targetPort: 4210, isProxied: false)
    .WithExternalHttpEndpoints()
    .WithNpm(installArgs: ["--no-audit"])
    .WaitFor(api);

builder
    .AddJavaScriptApp(
        "sample-project-management-workspace",
        "../../../Front-end",
        runScriptName: "start:workspace")
    .WithReference(api)
    .WithHttpEndpoint(port: 4220, targetPort: 4220, isProxied: false)
    .WithExternalHttpEndpoints()
    .WithNpm(false)
    .WaitFor(api)
    .WaitFor(management);

builder.Build().Run();
