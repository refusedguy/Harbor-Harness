// Harbor.Extensions — root namespace file.
// This project is the infrastructure helper layer (ArrayPool, StringBuilderPool,
// FrozenSet materializers, MemoryPack round-trip helpers). Namespaces are
// preserved as Harbor.Abstractions.Extensions so that downstream consumers
// (Harbor.Application, Harbor.Tools.*, etc.) require zero `using` changes —
// they keep referencing `Harbor.Abstractions.Extensions` and pick up the types
// transitively via the Harbor.Abstractions facade.

global using System;
global using System.Collections.Generic;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
global using CSharpFunctionalExtensions;
