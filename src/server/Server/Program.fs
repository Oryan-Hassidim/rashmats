module Program

open System
open Azure.Monitor.OpenTelemetry.Exporter
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Builder
open Microsoft.Azure.Functions.Worker.OpenTelemetry
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open global.OpenTelemetry

[<EntryPoint>]
let main args =
    let builder = FunctionsApplication.CreateBuilder args

    builder.ConfigureFunctionsWebApplication()
    |> ignore

    if not (String.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING"))) then
        builder
            .Services
            .AddOpenTelemetry()
            .UseFunctionsWorkerDefaults()
            .UseAzureMonitorExporter()
        |> ignore

    builder.Build().Run()

    // let host =
    //     HostBuilder()
    //         .ConfigureFunctionsWebApplication()
    //         .ConfigureServices(fun services ->
    //             if
    //                 not
    //                     (
    //                         System.String.IsNullOrEmpty(
    //                             System.Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")
    //                         )
    //                     )
    //             then
    //                 services
    //                     .AddOpenTelemetry()
    //                     .UseFunctionsWorkerDefaults()
    //                     .UseAzureMonitorExporter()
    //                 |> ignore)
    //         .Build()

    // If using the Cosmos DB, Blob or Tables extension, you need to configure the extensions manually using the extension methods below.
    // Learn more about this here: https://go.microsoft.com/fwlink/?linkid=2245587
    // ConfigureFunctionsWebApplication(fun (context: HostBuilderContext) (appBuilder: IFunctionsWorkerApplicationBuilder) ->
    //     appBuilder.ConfigureCosmosDBExtension() |> ignore
    //     appBuilder.ConfigureBlobStorageExtension() |> ignore
    //     appBuilder.ConfigureTablesExtension() |> ignore
    // ) |> ignore

    // host.Run()
    0
