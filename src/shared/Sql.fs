namespace Rashmatz.Shared

open System.Collections.Concurrent
open System.IO
open System.Reflection
open Donald

/// Loads compile-time embedded `.sql` files from the assembly and provides concise Donald parameter helpers.
module Sql =
    let private cache = ConcurrentDictionary<string, string>()

    let load (logicalName: string) : string =
        cache.GetOrAdd(
            logicalName,
            fun name ->
                let asm = Assembly.GetExecutingAssembly()
                use stream = asm.GetManifestResourceStream name

                match stream with
                | null -> failwith $"Embedded SQL resource '{name}' not found."
                | s ->
                    use reader = new StreamReader(s)
                    reader.ReadToEnd()
        )

    let strOpt =
        function
        | Some (s: string) -> SqlType.String s
        | None -> SqlType.Null
