// Global usings for the Harbor.Plugins.* layer project.
// CSharpFunctionalExtensions (Result<T>) is the only non-framework using
// required across the layer — interfaces and base types come from
// Harbor.Plugins.Abstractions via ProjectReference (no namespace import needed
// when the consumer file adds `using Harbor.Plugins.Abstractions;` at the top).
global using CSharpFunctionalExtensions;
