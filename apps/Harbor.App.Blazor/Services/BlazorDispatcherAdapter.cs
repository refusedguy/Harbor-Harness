using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Harbor.App.Blazor.Services;

/// <summary>
///     Marshals <see cref="Action"/> callbacks onto the Blazor render
///     (synchronisation) context. Use whenever a background thread (event bus,
///     timer, agent loop) needs to re-render a component — calling
///     <see cref="ComponentBase.StateHasChanged"/> directly from a non-render
///     thread throws <see cref="InvalidOperationException"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists:</b> the <see cref="UiStore"/> raises
///         <see cref="UiStore.Changed"/> from arbitrary thread-pool threads
///         (the agent loop publishes via <c>Channel&lt;T&gt;</c> readers).
///         Razor components need to call <c>StateHasChanged</c> on the
///         render thread, so they subscribe to <c>Changed</c> and route the
///         notification through this adapter.
///     </para>
///     <para>
///         Implemented as a singleton that holds a single render-context
///         delegate. This is acceptable because Harbor.Blazor is a desktop
///         single-user app — one browser tab = one circuit = one render
///         context. If the app is ever hosted multi-tenant, swap this for a
///         per-circuit scoped adapter.
///     </para>
/// </remarks>
public sealed class BlazorDispatcherAdapter
{
    private Func<Action, Task>? _invokeAsync;

    /// <summary>
    ///     Bind the adapter to the current render context. Called once from
    ///     the layout's <c>OnInitialized</c> via <c>Dispatcher.Bind(a =&gt;
    ///     InvokeAsync(a))</c>. After this call, every
    ///     <see cref="InvokeAsync(Action)"/> routes through the Blazor
    ///     render thread.
    /// </summary>
    /// <param name="invokeAsync">
    ///     A delegate that runs an action on the render thread (typically
    ///     <c>a =&gt; InvokeAsync(a)</c> from a <c>ComponentBase</c>).
    /// </param>
    public void Bind(Func<Action, Task> invokeAsync)
    {
        _invokeAsync = invokeAsync;
    }

    /// <summary>Invoke an action on the render thread. Returns immediately if no dispatcher is bound yet.</summary>
    /// <param name="action">The action to invoke.</param>
    /// <returns>A <see cref="Task"/> that completes when the action has run on the render thread.</returns>
    public Task InvokeAsync(Action action)
    {
        var invoke = _invokeAsync;
        if (invoke is null)
        {
            // Pre-render or SSR-only — just run inline.
            action();
            return Task.CompletedTask;
        }
        return invoke(action);
    }

    /// <summary>Invoke a function on the render thread. Returns the function's result.</summary>
    /// <typeparam name="T">Return type.</typeparam>
    /// <param name="func">The function to invoke.</param>
    /// <returns>The function's return value, marshalled to the render thread.</returns>
    public async Task<T> InvokeAsync<T>(Func<T> func)
    {
        T result = default!;
        await InvokeAsync(() => { result = func(); }).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
///     Razor interop shim. Wraps <c>IJSRuntime</c> calls for Monaco, theme
///     switching, and clipboard. Scoped to the circuit because
///     <c>IJSRuntime</c> is circuit-scoped.
/// </summary>
public sealed class HarborJsInterop
{
    private readonly IJSRuntime _js;

    /// <summary>Construct the interop.</summary>
    /// <param name="js">The <c>IJSRuntime</c> for this circuit.</param>
    public HarborJsInterop(IJSRuntime js)
    {
        _js = js;
    }

    /// <summary>Initialise a Monaco editor instance on the supplied element.</summary>
    /// <param name="container">Element id of the host div.</param>
    /// <param name="initialValue">Initial text content.</param>
    /// <param name="language">Language id (csharp, typescript, markdown, json).</param>
    /// <param name="theme">Monaco theme name ("vs-dark" by default).</param>
    /// <returns>A task that completes when the editor is ready.</returns>
    public ValueTask InitMonacoAsync(string container, string initialValue, string language, string theme = "vs-dark")
        => _js.InvokeVoidAsync("harborMonaco.init", container, initialValue, language, theme);

    /// <summary>Get the current editor value.</summary>
    /// <param name="container">Element id of the host div.</param>
    /// <returns>The current text content of the editor.</returns>
    public ValueTask<string> GetMonacoValueAsync(string container)
        => _js.InvokeAsync<string>("harborMonaco.getValue", container);

    /// <summary>Set the editor value programmatically.</summary>
    /// <param name="container">Element id of the host div.</param>
    /// <param name="value">The new text content.</param>
    public ValueTask SetMonacoValueAsync(string container, string value)
        => _js.InvokeVoidAsync("harborMonaco.setValue", container, value);

    /// <summary>Set the editor language (e.g. when switching tabs).</summary>
    public ValueTask SetMonacoLanguageAsync(string container, string language)
        => _js.InvokeVoidAsync("harborMonaco.setLanguage", container, language);

    /// <summary>Dispose the editor instance and free JS resources.</summary>
    public ValueTask DisposeMonacoAsync(string container)
        => _js.InvokeVoidAsync("harborMonaco.dispose", container);

    /// <summary>Render a Chart.js chart in the supplied canvas.</summary>
    /// <param name="canvasId">Canvas element id.</param>
    /// <param name="config">JSON-serialised Chart.js config object.</param>
    public ValueTask RenderChartAsync(string canvasId, string config)
        => _js.InvokeVoidAsync("harborCharts.render", canvasId, config);

    /// <summary>Copy the supplied text to the system clipboard.</summary>
    public ValueTask CopyToClipboardAsync(string text)
        => _js.InvokeVoidAsync("harborInterop.copyToClipboard", text);

    /// <summary>Scroll the element with the supplied id to its bottom.</summary>
    public ValueTask ScrollToBottomAsync(string elementId)
        => _js.InvokeVoidAsync("harborInterop.scrollToBottom", elementId);

    /// <summary>Apply the named theme to the document root (data-theme attribute).</summary>
    public ValueTask ApplyThemeAsync(string themeName)
        => _js.InvokeVoidAsync("harborInterop.applyTheme", themeName);
}
