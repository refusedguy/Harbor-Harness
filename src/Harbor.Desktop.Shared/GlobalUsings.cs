// GlobalUsings.cs — solution-wide implicit usings for Harbor.Desktop.Shared.
global using System;
global using System.Collections.Generic;
global using System.Globalization;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
// #754: `global using Microsoft.Extensions.Logging;` removed with its
// PackageReference — it existed only to resolve that package for the deleted
// RecentItemsService, and no file here names a type from it.
