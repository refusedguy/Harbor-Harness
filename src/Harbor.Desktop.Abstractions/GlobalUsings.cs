// GlobalUsings.cs — the leaf's global usings.
//
// #1899: this file used to carry the view-models' usings (CommunityToolkit.Mvvm,
// Harbor.Ui.Framework.*, Microsoft.Extensions.Logging). Those moved to
// Harbor.Desktop.ViewModels with the view-models. What is left is what the
// leaf's own four folders need: the immutable collections CommonConfig holds
// its API-key and permission lists in.

global using System.Collections.Immutable;
