namespace Company.Function

open Microsoft.Azure.Functions.Worker
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Mvc
open System.Threading.Tasks

module HelloWorld =

    [<Function("HelloWorld")>]
    let run
        ([<HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)>] req: HttpRequest)
        (context: FunctionContext)
        : Task<IActionResult> =
        task {
            let logger = context.GetLogger "HelloWorld"
            logger.LogInformation "F# HTTP trigger function processed a request"

            return OkObjectResult("Hello, World!") :> IActionResult
        }
