// Harbor.Abstractions — root namespace file
// This file deliberately left empty; uses file-scoped namespaces per module.

global using System;
global using System.Collections.Generic;
global using System.Text.Json;
global using System.Threading;
global using System.Threading.Tasks;
global using CSharpFunctionalExtensions;
// ZLinq replaces System.Linq throughout Harbor.Abstractions for zero-allocation LINQ.
// The SDK implicit "System.Linq" using is removed in Harbor.Abstractions.csproj.
global using ZLinq;
