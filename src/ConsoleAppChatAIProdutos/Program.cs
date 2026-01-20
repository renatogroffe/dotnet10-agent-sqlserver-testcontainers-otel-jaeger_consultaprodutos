using Azure.AI.OpenAI;
using Bogus;
using ConsoleAppChatAIProdutos.Data;
using ConsoleAppChatAIProdutos.Inputs;
using ConsoleAppChatAIProdutos.Plugins;
using ConsoleAppChatAIProdutos.Tracing;
using ConsoleAppChatAIProdutos.Utils;
using LinqToDB;
using LinqToDB.Data;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.ClientModel;
using Testcontainers.MsSql;

Console.WriteLine("***** Testes com Agent Framework + Plugins (Functions) + SQL Server *****");
Console.WriteLine();

var numberOfRecords = InputHelper.GetNumberOfNewProducts();

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

CommandLineHelper.Execute("docker images",
    "Imagens antes da execucao do Testcontainers...");
CommandLineHelper.Execute("docker container ls",
    "Containers antes da execucao do Testcontainers...");

Console.WriteLine("Criando container para uso do SQL Server...");
var msSqlContainer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2025-latest")
    .Build();
await msSqlContainer.StartAsync();
Console.WriteLine("Criando banco de dados BaseCatalogo...");
await msSqlContainer.ExecScriptAsync(
    @"
    CREATE DATABASE BaseCatalogo;
    GO

    USE BaseCatalogo;
    GO

    CREATE TABLE Produtos (
        Id INT IDENTITY(1,1) NOT NULL,
        CodigoBarras VARCHAR(13) NOT NULL,
        Nome VARCHAR(100) NOT NULL,
        Preco NUMERIC(19, 4) NOT NULL,
        CONSTRAINT PK_Produtos PRIMARY KEY (Id)
    );
");

CommandLineHelper.Execute("docker images",
    "Imagens apos execucao do Testcontainers...");
CommandLineHelper.Execute("docker container ls",
    "Containers apos execucao do Testcontainers...");

var connectionString = msSqlContainer.GetConnectionString().Replace("=master;", "=BaseCatalogo;");
Console.WriteLine($"Connection String da base de dados SQL: {connectionString}");
CatalogoContext.ConnectionString = connectionString;

var db = new DataConnection(new DataOptions().UseSqlServer(connectionString));

var random = new Random();
var fakeProdutos = new Faker<ConsoleAppChatAIProdutos.Data.Fake.Produto>("pt_BR").StrictMode(false)
            .RuleFor(p => p.Nome, f => f.Commerce.Product())
            .RuleFor(p => p.CodigoBarras, f => f.Commerce.Ean13())
            .RuleFor(p => p.Preco, f => random.Next(10, 30))
            .Generate(numberOfRecords);
Console.WriteLine($"Gerando {numberOfRecords} produtos...");
await db.BulkCopyAsync<ConsoleAppChatAIProdutos.Data.Fake.Produto>(fakeProdutos);
Console.WriteLine($"Produtos gerados com sucesso!");
Console.WriteLine();
var resultSelectProdutos = await msSqlContainer.ExecScriptAsync(
    "USE BaseCatalogo; SELECT * FROM dbo.Produtos;");
Console.WriteLine(resultSelectProdutos.Stdout);

var resourceBuilder = ResourceBuilder
    .CreateDefault()
    .AddService(OpenTelemetryExtensions.ServiceName);

var traceProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    .AddSource(OpenTelemetryExtensions.ServiceName)
    .AddEntityFrameworkCoreInstrumentation()
    .AddHttpClientInstrumentation()
    .AddOtlpExporter(cfg =>
    {
        cfg.Endpoint = new Uri(configuration["OtlpExporter:Endpoint"]!);
    })
    .Build();

var agent = new AzureOpenAIClient(endpoint: new Uri(configuration["AzureOpenAI:Endpoint"]!),
        credential: new ApiKeyCredential(configuration["AzureOpenAI:ApiKey"]!))
    .GetChatClient(configuration["AzureOpenAI:DeploymentName"]!)
    .CreateAIAgent(
        instructions: "Você é um assistente de IA que ajuda o usuario a consultar informações" +
            "sobre produtos em uma base de dados do SQL Server.",
        tools: [.. ProdutosPlugin.GetFunctions()])
    .AsBuilder()
    .UseOpenTelemetry(sourceName: OpenTelemetryExtensions.ServiceName)
    .Build();

var oldForegroundColor = Console.ForegroundColor;
while (true)
{
    Console.WriteLine("Sua pergunta:");
    var userPrompt = Console.ReadLine();

    using var activity1 = OpenTelemetryExtensions.ActivitySource
        .StartActivity("PerguntaChatIAProdutos")!;

    var result = await agent.RunAsync(userPrompt!);

    Console.WriteLine();
    Console.WriteLine("Resposta da IA:");
    Console.WriteLine();

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine(result.AsChatResponse().Messages.Last().Text);
    Console.ForegroundColor = oldForegroundColor;

    Console.WriteLine();
    Console.WriteLine();

    activity1.Stop();
}