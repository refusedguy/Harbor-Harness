# Fix: Avalonia resources not registered

Add to apps/Harbor.App.Avalonia/Harbor.App.Avalonia.csproj:

```xml
  <ItemGroup>
    <AvaloniaResource Include="Views\**\*.axaml" />
    <AvaloniaResource Include="App.axaml" />
  </ItemGroup>
```
