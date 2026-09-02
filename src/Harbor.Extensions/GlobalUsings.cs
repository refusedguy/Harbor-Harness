// Harbor.Extensions — root namespace file.
// This project is the infrastructure helper layer (ArrayPool, StringBuilderPool,
// FrozenSet materializers, MemoryPack round-trip helpers). Zero Harbor project
// references and zero transitive package dependencies on Harbor code — consumers
// that use the helpers reference Harbor.Extensions directly.

global using System;
global using System.Collections.Generic;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
