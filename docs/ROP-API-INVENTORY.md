# CSharpFunctionalExtensions 3.7.0 — API inventory (pinned)

> **Status:** reference document. Not a policy — [issue #571](https://github.com/refusedguy/Harbor-Harness/issues/571)
> is the policy, and this is the ground truth the ROP sweepers measure against.
>
> **Read §3 (ranked "most likely reinvented") first.** The type-by-type reference in §2
> is for a reader who has to adopt a specific member; §3 is the search key.

---

## 1. Pinned version + provenance

### Version

| | |
|---|---|
| **Pinned version** | **`3.7.0`** |
| Declared at | [`Directory.Packages.props:167`](../Directory.Packages.props) — `<PackageVersion Include="CSharpFunctionalExtensions" Version="3.7.0" />` |
| Consumed by | 13 projects via `<PackageReference Include="CSharpFunctionalExtensions"/>` (central version management, no inline version) — `Harbor.Abstractions`, `Harbor.Abstractions.Contracts`, `Harbor.Desktop.Abstractions`, `Harbor.Ipc.Abstractions`, `Harbor.Ui.Framework.State`, `Harbor.Ui.Framework.Sessions`, `Harbor.Lsp`, `Harbor.Terminal.Pty`, `Harbor.Scripting.Abstractions`, `Harbor.Scripting.Bridge`, plus 3 test projects |
| TFMs shipped | `net6.0`, `net8.0`, `netstandard2.0` (all three present in the cache; Harbor builds `net8.0`/`net10.0`) |
| License | MIT |

**Never document `latest`.** Everything below is what `3.7.0` actually ships.

### The two sources I cross-checked

| # | Source | What it gave | Trust |
|---|---|---|---|
| **A** | The shipped XML doc file in the local NuGet cache: `~/.nuget/packages/csharpfunctionalextensions/3.7.0/lib/net8.0/CSharpFunctionalExtensions.xml` (6187 lines) | 1076 documented member IDs, grouped by owning type: `ResultExtensions` 291, `AsyncResultExtensions{Left,Right,Both}Operand` 107/85/85, `Result` 63, `MaybeExtensions` 48, `ValueTaskExtensions` 368, `UnitResult` 10, `Maybe` 5, … | Authoritative for *what exists*, **but names only** — an XML doc ID carries no return type, no `in`/`ref`, no nullability, no constraint. It cannot answer "is `MapError` on `Result<T>` `Func<string,string>` or `Func<string,T>`". |
| **B** | The source at the matching tag: `https://codeload.github.com/vkhorikov/CSharpFunctionalExtensions/tar.gz/refs/tags/v3.7.0` (342 source files under `CSharpFunctionalExtensions/`) | Exact signatures, constraints, `in`/`ref` modifiers, implicit operators, and the bodies that define the runtime behaviour of the guards | Authoritative for *semantics*. `version.txt` in that tree reads `3.7.0` — tag and package are the same commit. |

**Did they agree?** Yes, on membership — every public type and method in B appears in A's member-ID set, and A contains nothing that is absent from B. The one systematic difference is **coverage, not disagreement**: A omits undocumented public members. Concretely, B declares members A does not mention, so a doc-file-only inventory silently loses them:

- `Result<T>.TryGetValue(out T)`, `TryGetError(out string)`, `TryGetValue(out T, out string)`, `TryGetError(out string, out T)` — the four nullable-annotated `out` overloads in `Result/Methods/TryGet.cs`; only some appear in A.
- `Result<T>.GetValueOrDefault(T defaultValue = default)` — an **instance** method on `Result<T>`/`Result<T,E>`, distinct from the `GetValueOrDefault` *extension* of the same name.
- `Result.TryGetError(out string)` / `Result<T,E>.TryGetError(out E)`.
- `ResultExtensions.CombineInOrder` (28 declarations) and `AsyncResultExtensionsLeftOperand.CompleteInOrder<T>`.
- `Result.Failure` overloads documented in A appear in A; but `Result.ConvertFailure` is only partially in A (3 of 4).

Corollary for the sweepers: **an inventory built from the XML doc alone is incomplete.** Anyone re-deriving this from A must diff against B.

### Upstream README — used as a checklist, not as the inventory

The upstream README (both the `3.7.0` copy in the tag and the current `master` copy the owner read) is **a curated example list, not a reference**:

- Its `### Result` section shows only construction, `SuccessIf`/`FailureIf`, implicit conversion, `ToString`, `Map`, `MapError`. It never shows `Bind`, `Tap`, `Ensure`, `Finally`, `Match`, `Compensate`, `Check`, `Combine`, `ConvertFailure`, `WithTransactionScope` — all of which exist and are documented here.
- Its `Core Concepts` section names `Bind`, `Tap`, `Ensure`, `Finally`, `WithTransactionScope` in prose with no API example.
- It shows `Maybe` almost completely, and `Result` almost not at all — which is why a Result-only audit concluded "nothing to do" (#571).

**README claims that are FALSE for `3.7.0`** (they are true on `master`, i.e. post-3.7.0):

| README/master claim | Reality in 3.7.0 |
|---|---|
| `Error` is a type; `Maybe<Error>` is idiomatic | **No `Error` type exists.** Error is `string` for `Result`/`Result<T>`, and an arbitrary `E` for `Result<T,E>`/`UnitResult<E>`. |
| `UnitResult` / `UnitResult<Error>` | `UnitResult<E>` **does exist** (typed-error, value-less). `UnitResult<Error>` does not, because `Error` does not. `UnitResult` (bare) is a **static factory class**, not a type. |
| `Result<T, E>` | **Exists**, in the full 3.7.0 sense. |
| `Result.Combine(name, email)` | No such overload. `Result.Combine` takes *results*, not named values: `Combine(params Result[])` / `Combine<T>(params Result<T>[])` / `Combine(IEnumerable<Result>, string separator)`. |
| `Maybe<T>` implicitly converts to `null`/`default` | **No.** The only implicit conversions are `T → Maybe<T>` and `Maybe (marker) → Maybe<T>.None`. There is **no** `Maybe<T> → T?`. Use `AsNullable()` or `GetValueOrDefault()`. |

---

## 2. Type-by-type reference

Convention used throughout: **use when** = the case the library is designed for; **do not use when** = the case where it is the wrong tool and a reviewer should push back.

Every combinator in this library has four generated shapes. There is no partial support: if you use one, all four exist and all four are AOT-safe and allocation-comparable.

| suffix | receiver | meaning |
|---|---|---|
| *(none)* | `Result` / `Maybe` | sync |
| `.Task` | `Task<Result>` / `Task<Maybe>` | async, receiver is already a task |
| `.Task.Left` | `Result` | async selector, result is the left operand |
| `.Task.Right` | `Result` | async selector, result is the right operand |
| `.ValueTask.*` | same as `.Task.*` | `ValueTask` variants of all four |

Source file naming mirrors this: `Map.cs` / `Map.Task.cs` / `Map.Task.Left.cs` / `Map.Task.Right.cs` / `Map.ValueTask{,.Left,.Right}.cs`.

### 2.1 `Result` — value-less, error is `string`

`src/CSharpFunctionalExtensions/Result/Result.cs`

```csharp
[Serializable] public readonly struct Result : IResult, ISerializable, IError<string>
{
    public bool IsFailure { get; }
    public bool IsSuccess { get; }          // => !IsFailure
    public string Error { get; }            // THROWS ResultSuccessException on success
    public override string ToString();      // "Success" | "Failure(<error>)"
    public static implicit operator UnitResult<string>(Result);
}
```

Static factory surface (partial struct — the factories live on `Result` itself):

```csharp
// construction
static Result    Success();
static Result<T> Success<T>(T value);
static Result<T,E> Success<T,E>(T value);
static UnitResult<E> Success<E>();                       // via UnitResult, see §2.4

static Result      Failure(string error);
static Result<T>   Failure<T>(string error);
static Result<T,E> Failure<T,E>(E error);                // via UnitResult? no — on Result

// conditional construction  (bool, Func<bool>, Func<Task<bool>>, Func<ValueTask<bool>>)
static Result        SuccessIf(bool isSuccess, string error);
static Result        SuccessIf(Func<bool> predicate, string error);
static Result<T>     SuccessIf<T>(bool isSuccess, in T value, string error);
static Result<T>     SuccessIf<T>(Func<bool> predicate, in T value, string error);
static Result<T,E>   SuccessIf<T,E>(bool isSuccess, in T value, in E error);
static Result        FailureIf(bool isFailure, string error);      // == SuccessIf(!c, e)
static Result<T>     FailureIf<T>(bool isFailure, T value, string error);
// … same four shapes for Func<bool>, Task<bool>, ValueTask<bool>

// lifting
static Result<T>   Of<T>(T value) where T : notnull;
static Result<T>   Of<T>(Func<T> func) where T : notnull;
static Task<Result<T>> Of<T>(Task<T> valueTask) where T : notnull;
static Task<Result<T>> Of<T>(Func<Task<T>> valueTaskFunc) where T : notnull;
static Result<T,E> Of<T,E>(T value) where T : notnull;         // + Func<T>, Task<T>, Func<Task<T>>

// exception capture
static Result       Try(Action action, Func<Exception,string> errorHandler = null);
static Result<T>    Try<T>(Func<T> func, Func<Exception,string> errorHandler = null);
static Result<T,E>  Try<T,E>(Func<T> func, Func<Exception,E> errorHandler);
static UnitResult<E> Try<E>(Action action, Func<Exception,E> errorHandler);
static Task<UnitResult<E>> Try<E>(Func<Task> action, Func<Exception,E> errorHandler);

// aggregation
static Result Combine(params Result[] results);
static Result Combine(IEnumerable<Result> results, string errorMessagesSeparator = null);
static Result Combine(string errorMessagesSeparator, params Result[] results);
static Result Combine<T>(params Result<T>[] results);
static Result Combine<T>(IEnumerable<Result<T>> results, string errorMessagesSeparator = null);
static Result Combine<T>(string sep, params Result<T>[] results);
static UnitResult<E> Combine<E>(params UnitResult<E>[] results) where E : ICombine;
static UnitResult<E> Combine<E>(IEnumerable<UnitResult<E>> results) where E : ICombine;
static UnitResult<E> Combine<E>(IEnumerable<UnitResult<E>>, Func<IEnumerable<E>,E> composerError);
static UnitResult<E> Combine<E>(Func<IEnumerable<E>,E> composerError, params UnitResult<E>[]);
static Result<bool,E> Combine<T,E>(params Result<T,E>[] results) where E : ICombine;
static Result<bool,E> Combine<T,E>(IEnumerable<Result<T,E>> results) where E : ICombine;
static Result<bool,E> Combine<T,E>(IEnumerable<Result<T,E>>, Func<IEnumerable<E>,E>);
static Result<bool,E> Combine<T,E>(Func<IEnumerable<E>,E>, params Result<T,E>[]);

static Result FirstFailureOrSuccess(params Result[] results);

static class Configuration {
    static string ErrorMessagesSeparator = ", ";
    static bool   DefaultConfigureAwait = false;
    static Func<Exception,string> DefaultTryErrorHandler = exc => exc.Message;
}
```

**Semantics / behaviour that matters:**

- `.Value` on `Result<T>` **throws `ResultFailureException(Error)`** on failure. `.Error` **throws `ResultSuccessException`** on success. Both are guarded, not `default`-returning.
- The constructor is *itself* a guard: `ErrorStateGuard` throws `ArgumentNullException` if you build a failure with a null/empty error, and `ArgumentException` if you build a success with a non-default error. So `Result.Failure<string>("")` **throws**, it does not produce a failed result with an empty message. This is the #561 bug class.
- `Combine` **concatenates all** error messages (deduplicated with an `×N` count) and **evaluates the whole collection eagerly** — it is not short-circuiting. All failures are reported, which is what a validation method wants and what a "fail fast" loop does not.
- `Combine` over `Result<T>` **discards every value**. To keep the values use the extension `Combine(this IEnumerable<Result<T>>, Func<IEnumerable<T>,K> composer)`.
- `Combine<T,E>` returns `Result<bool,E>` — upstream has a `// TODO: Ideally, we would be using BaseResult<E>` on those exact lines. The `bool` is arbitrary. Do not read it.
- `Of` does **not** catch exceptions (it is `Result.Success` with a `notnull` constraint); `Try` is the one that catches.
- `FirstFailureOrSuccess` is `Combine` semantics but returns only the **first** failure, un-joined.

**Use when:** validation and I/O that can fail with a message. `Result` (value-less) is the right shape for a method whose job is to check and bail.

**Do not use when:**
- The failure is not an error, just absence (missing key, empty search hit) → **`Maybe`**. See §5.
- You need a typed error code/union → `Result<T, E>` / `UnitResult<E>` (§2.4). Harbor today uses `string` everywhere, which is the weakest link.
- You want the value-less "no value, no error" case → `Maybe<T>.None` or `Result<Maybe<T>>`.

### 2.2 `Result<T>` — value-carrying, error is `string`

`src/CSharpFunctionalExtensions/Result/ResultT.cs`

```csharp
[Serializable] public readonly struct Result<T> : IResult<T>, ISerializable
{
    public bool IsFailure { get; }
    public bool IsSuccess { get; }
    public string Error { get; }            // throws on success
    public T Value { get; }                 // throws ResultFailureException on failure
    public T GetValueOrDefault(T defaultValue = default);   // INSTANCE method
    public bool TryGetValue([NotNullWhen(true)] out T value);
    public bool TryGetError([NotNullWhen(true)] out string error);
    public bool TryGetValue(out T value, [NotNullWhen(false)] out string error);
    public bool TryGetError(out string error, [NotNullWhen(false)] out T value);
    public override string ToString();      // "Success(<value>)" | "Failure(<error>)"

    public static implicit operator Result<T>(T value);
    public static implicit operator Result(Result<T> result);
    public static implicit operator UnitResult<string>(Result<T> result);
}
```

plus `Result<T>.ConvertFailure()` → `Result`, and `Result<T>.ConvertFailure<K>()` → `Result<K>` (both **throw `InvalidOperationException` on success**).

**Use when:** the call can fail *and* you need its value on the happy path. This is the default in this repo (432 non-test `Result<` mentions).

**Do not use when:**
- You are going to `if (r.IsFailure) … ; use(r.Value)` — that is a `Map`/`Bind`/guard ladder, not a `Result<T>` you should hand-build. See §3.1.
- The "value" is a nullable reference used as an error channel. That is a `Result` with no value, or a real error type.

### 2.3 `Result<T, E>` — typed error

`src/CSharpFunctionalExtensions/Result/ResultTE.cs`

```csharp
[Serializable] public readonly struct Result<T, E> : IResult<T, E>, ISerializable
{
    public bool IsFailure { get; } public bool IsSuccess { get; }
    public E Error { get; }                 // throws on success
    public T Value { get; }                 // throws ResultFailureException<E> on failure
    public T GetValueOrDefault(T defaultValue = default);
    public bool TryGetValue(out T value);
    public bool TryGetError([NotNullWhen(true)] out E error);
    public bool TryGetValue(out T value, [NotNullWhen(false)] out E error);
    public bool TryGetError(out E error, [NotNullWhen(false)] out T value);
    public Result<K,E> ConvertFailure<K>();

    public static implicit operator Result<T,E>(T value);
    public static implicit operator Result<T,E>(E error);      // ⚠ see §5.4
    public static implicit operator UnitResult<E>(Result<T,E> result);
}
```

**Use when:** the error is part of the domain and you want it pattern-matchable. `Result<T,E>` is the type that makes `error switch { ConfigError e => … }` possible; upstream `Combine` has an `ICombine` interface precisely for this.

**Do not use when:** you have no real error type to name. `Result<T, string>` is a pointless duplicate of `Result<T>` and it makes every combinator call ambiguous. Harbor uses 0 occurrences — a deliberate or accidental consequence, but the reason `string` errors cannot be told apart anywhere in the codebase.

### 2.4 `UnitResult<E>` and the `UnitResult` factory class

`src/CSharpFunctionalExtensions/Result/UnitResult.cs`

```csharp
[Serializable] public readonly struct UnitResult<E> : IUnitResult<E>, ISerializable
{
    public bool IsFailure { get; } public bool IsSuccess { get; }
    public E Error { get; }                 // throws on success
    public bool TryGetError([NotNullWhen(true)] out E error);
    public Result<K,E> ConvertFailure<K>(); // throws InvalidOperationException on success
    public override string ToString();
    public static implicit operator UnitResult<E>(E error);
}

public static class UnitResult {              // "Alternative entrypoint for UnitResult<E> to avoid ambiguous calls"
    static UnitResult<E> Failure<E>(in E error);
    static UnitResult<E> Success<E>();
    static UnitResult<E> SuccessIf<E>(bool isSuccess, in E error);
    static UnitResult<E> SuccessIf<E>(Func<bool> predicate, in E error);
    static UnitResult<E> FailureIf<E>(bool isFailure, in E error);
    static UnitResult<E> FailureIf<E>(Func<bool> predicate, in E error);
    static Task<UnitResult<E>> SuccessIf<E>(Func<Task<bool>>, in E);
    static Task<UnitResult<E>> FailureIf<E>(Func<Task<bool>>, in E);
}
```

**⚠ `UnitResult` (bare) is a static class, not a type.** `UnitResult<string>` is a real type; you cannot declare a variable of type `UnitResult`. It exists as a "parallel entry point" precisely because `Result.Success<E>()` and `Result.Failure<E>(…)` would be ambiguous with the value-carrying overloads.

**Use when:** you have a typed error and a command that returns nothing on success — validation, `ValidateAsync`, `Delete`, `Save`. It documents "this method never produces a value" without resorting to `Result<bool>`.

**Do not use when:** your error is `string`. `UnitResult<string>` is bit-identical to `Result`, and Harbor's 0 occurrences of `UnitResult` are the correct outcome for a `string`-error codebase.

### 2.5 `Maybe<T>` and the `Maybe` factory struct

`src/CSharpFunctionalExtensions/Maybe/Maybe.cs`

```csharp
[Serializable] public readonly struct Maybe<T> : IEquatable<Maybe<T>>, IEquatable<object>, IMaybe<T>
{
    public T  Value { get; }                       // throws InvalidOperationException("Maybe has no value.")
    public T  GetValueOrThrow(string? errorMessage = null);
    public T  GetValueOrThrow(Exception exception);
    public T  GetValueOrDefault(T defaultValue);
    public T? GetValueOrDefault();                 // returns T?
    public bool TryGetValue([NotNullWhen(true)] out T? value);
    public static Maybe<T> None { get; }
    public bool HasValue { get; }                  // [MemberNotNullWhen(true, "_value")]
    public bool HasNoValue { get; }                // [MemberNotNullWhen(false, "_value")]
    public static Maybe<T> From(T? value);
    public static Maybe<T> From(Func<T?> func);
    public static Task<Maybe<T>> From(Task<T?> valueTask);
    public static Task<Maybe<T>> From(Func<Task<T?>> valueTaskFunc);
    public static implicit operator Maybe<T>(T? value);
    public static implicit operator Maybe<T>(Maybe _);     // non-generic marker → None
    // == / != against Maybe<T>, T?, object; Equals; GetHashCode; ToString
}

public readonly struct Maybe {                    // non-generic factory, NOT a type you hold
    public static Maybe None { get; }
    public static Maybe<T> From<T>(T? value);
    public static Maybe<T> From<T>(Func<T?> func);
    public static Task<Maybe<T>> From<T>(Task<T?> valueTask);
    public static Task<Maybe<T>> From<T>(Func<Task<T?>> valueTaskFunc);
}

public interface IMaybe<out T> { T Value { get; } bool HasValue { get; } bool HasNoValue { get; } }
```

**Semantics:**

- `Maybe<T>` is a **null-aware container**, not an error channel. `Maybe.From(null)` ⇒ `None`. `HasValue`/`HasNoValue` carry `[MemberNotNullWhen]`/`[MaybeNullWhen]`, so the nullable flow analysis **knows** `_value` is non-null after `if (m.HasValue)`.
- Equality: `None == null` is `true`; `None == None` is `true`; `GetHashCode()` is `0` for `None`.
- `ToString()` is `"No value"` when empty, else the value's `ToString()`.

**Use when — this is the single most important section of this document for #562/#559/#571:**

- "**Absent**" rather than "**failed**": a missing config key, an empty search result, an optional row, "the user pressed nothing", "this optional flag was not set".
- Declaring a member as `Maybe<T>` **removes the need for `!`**. That is the mechanical fix for the nullable lies in #562 (`HarborCompositionContext`'s 5 `null!` properties) and #559 (`IAgent.State` non-nullable but null until `Initialize` — **landed**, see §4 item 8).
- `Maybe<T>` is a **struct**, so an empty property pattern never detects absence: `x is not { }` always matches and binds the WRAPPER. Test `HasNoValue` / `HasValue` (or `{ HasValue: true, Value: var v }`).
- A `Maybe<T>` **in a signature means the caller must handle the `None` branch**, at compile time. `T?` does not.

**Do not use when:**

- **The absence is an error the user should be told about.** `Maybe` swallows the reason. `file not found` in a tool result is a `Result` with a message, not `Maybe<Path>`. Using `Maybe` here is the mistake the README calls out: *"Result used where Maybe is right"* is bad; *`Maybe` used where `Result` is right* is equally bad and loses the diagnostic.
- You need to distinguish "absent" from "the query itself failed". That is `Result<Maybe<T>>` — the shape `LspServerSession.cs` already uses, and the shape `MaybeExtensions.Optional` exists to build.
- **There is no `Either<L,R>` in this library.** `Either` is not in 3.7.0 (verified against the tag's file list: no `Either*` file, no `Either` string in the metadata heap). If you need it, it is `Result<T,E>` for success-or-error, or a hand-rolled discriminated record. Do not go looking for it.
- **There is no `Unit` type.** `UnitResult<E>` is the "no value" result, and it is not interchangeable with `Unit`.

### 2.6 `MaybeExtensions` — the whole `Maybe` combinator set

| member | semantics | use when | do not use when |
|---|---|---|---|
| `Map<T,K>(Maybe<T>, Func<T,K>) → Maybe<K>` | transform the present value | "absent in, absent out, but re-shape" | you need to fail ⇒ combine with `ToResult` |
| `Select<T,K>` | **alias of `Map`** — exists for LINQ query syntax | you are writing `from … select` | anywhere else; `Map` reads better in method chains |
| `Bind<T,K>(Maybe<T>, Func<T,Maybe<K>>) → Maybe<K>` | monadic chain | flattening nested absence | a plain transformation |
| `SelectMany<T,K>` / `SelectMany<T1,T2,K>` | `Bind`, aliased for LINQ | `from x in a from y in b select …` | anywhere else |
| `Where(Maybe<T>, Func<T,bool>) → Maybe<T>` | predicate filter; non-match ⇒ `None` | optional + conditional | filtering a collection ⇒ use `IEnumerable` LINQ |
| `Choose<T,U>(IEnumerable<Maybe<T>>) → IEnumerable<U>` | drop the `None`s, unwrap the rest | "give me the ones that exist" | you need to know *how many* were dropped |
| `Choose<T,U>(IEnumerable<Maybe<T>>, Func<T,U>)` | as above + transform | as above + reshape | |
| `Execute(Maybe<T>, Action<T>)` | side effect **only if present** | fire-and-observe logging on the happy path | you need the side effect to happen on `None` too |
| `ExecuteNoValue(Maybe<T>, Action)` | side effect **only if absent** | "not found" diagnostics | |
| `Tap(Maybe<T>, Action<T>) → Maybe<T>` | side effect, returns the *same* `Maybe` | inserting a log/trace into a chain without breaking it | replacing `Execute` — same behaviour, but chainable |
| `TapNoValue(Maybe<T>, Action) → Maybe<T>` | as above, on `None` | | |
| `Or(Maybe<T>, T fallback)` / `Or(Maybe<T>, Func<T>)` / `Or(Maybe<T>, Maybe<T>)` / `Or(Maybe<T>, Func<Maybe<T>>)` | supply a default | **replaces `x ?? default` and `if (m.HasNoValue) m = y;`** | a default that is itself expensive ⇒ lazy `Func` overload |
| `Match<TE>(Maybe<T>, Func<T,TE> Some, Func<TE> None) → TE` | total function out of a partial one | leaving the `Maybe` world at a boundary | inside a chain — it collapses to a plain value |
| `Match(Maybe<T>, Action<T>, Action)` | void `Match` | side effects on both branches | |
| `Flatten(Maybe<Maybe<T>>) → Maybe<T>` | collapse nesting | | rarely needed directly |
| `GetValueOrDefault(Maybe<T>, Func<T>)` | default, lazily computed | | |
| `GetValueOrDefault<T,K>(Maybe<T>, Func<T,K> selector, K default)` | map-with-default in one pass | `a.Count > 0 ? f(a[0]) : 0` | |
| `GetValueOrThrow(Maybe<T>, string)` / `(Maybe<T>, Exception)` | explicit crash | **only** where "absence is a bug" | anywhere the caller can handle it — the whole point of `Maybe` |
| `TryGetValue(Maybe<T>, out T?)` | non-throwing extraction | interop at a non-`Maybe` boundary | in new code — it re-introduces the `out` ladder |
| `AsMaybe<T>(T? value)` / `AsMaybe(Nullable<T>)` / `AsMaybe(Task<T>)` | lift a nullable into a `Maybe` | bridging an existing nullable-returning API | |
| `AsNullable<T>(Maybe<T>) → T?` | back to a nullable | returning to a caller that wants `T?` | if the caller can take `Maybe<T>` — that is the better signature |
| `ToList(Maybe<T>) → List<T>` | 0- or 1-element list | LINQ interop | |
| `Deconstruct(Maybe<T>, out bool hasValue, out T value)` | pattern matching | `var (has, v) = m;` | |
| `ToResult<T>(Maybe<T>, string errorMessage) → Result<T>` | absence ⇒ **failure** with *your* message | at an API boundary where absence is an error | inside the domain — you would be discarding the reason you used `Maybe` |
| `ToResult<T,E>(Maybe<T>, E error)` / `(Maybe<T>, Func<E>)` | typed variant | when you have an error type | |
| `ToUnitResult<T,E>(Maybe<T>, E error)` | absence ⇒ typed failure, no value | | |
| `ToInvertedResult<T>(Maybe<T>, string errorMessage) → Result` | presence ⇒ **failure**, absence ⇒ success | "must NOT be present" checks (e.g. "this path must not exist") | never by accident — the polarity is inverted and reviewers will misread it |
| `BindOptional<T,K>(Maybe<Result<T>>, Func<T,Result<K>>) → Result<Maybe<K>>` | optional value into a `Result` whose value is optional | `Result<Maybe<T>>` plumbing | |
| `Optional<T>(Maybe<Result<T>>) → Result<Maybe<T>>` | "mark the possibly-null value as optional; null is not an error" | **exactly the primitive needed for the three nullable lies** | |

### 2.7 `ResultExtensions` — the whole `Result` combinator set

Grouped by what it does. All four async shapes exist for every one.

#### Constructing / inspecting

| member | semantics | use when | do not use when |
|---|---|---|---|
| `AsMaybe<T>(Result<T>) → Maybe<T>` | failure ⇒ `None` | dropping the error reason | the reason matters |
| `Deconstruct(Result, out bool isSuccess, out bool isFailure[, out string error])` and the `T` / `T,E` shapes | tuple deconstruction | `var (ok, v, err) = r;` | |
| `GetValueOrDefault<T>(Result<T>, Func<T>) → T` | non-throwing read with lazy default | bridging to a non-Result caller | inside the domain — that is a `Bind` to a plain value |
| `GetValueOrDefault<T,K>(Result<T>, Func<T,K> selector, K default)` | map-with-default | `r.IsSuccess ? f(r.Value) : 0` | |
| `Finally<T>(Result, Func<Result,T>) → T` | run a function on the `Result` **whatever** the state, return its value | "produce a UI row from a result" | — |
| `Match<TE>(Result<T>, Func<T,TE> onSuccess, Func<string,TE> onFailure) → TE` | total function out | leaving the `Result` world at a boundary | inside a chain |
| `Match(Result, Action, Action<string>)` | void `Match` | | |

#### Mapping

| member | semantics | use when | do not use when |
|---|---|---|---|
| `Map<T,K>(Result<T>, Func<T,K>) → Result<K>` | transform the value; **short-circuits on failure, does not invoke `func`** | `r.IsSuccess ? Result.Success(f(r.Value)) : r;` | the failure needs replacing — that is `MapError` / `Compensate` |
| `Map<K>(Result, Func<K>) → Result<K>` | give a value-less result a value | a factory that validates then produces | |
| `Map<T,K,E>(Result<T,E>, Func<T,K>) → Result<K,E>` | typed variant | | |
| `Map<K,E>(UnitResult<E>, Func<K>) → Result<K,E>` | | | |
| `MapError<Result>(Result, Func<string,string>)` | rewrite the error | adding context to a message; **the repo's #561 shape** | you want to *replace* failure with success — that is `Compensate` |
| `MapError<E>(Result, Func<string,E>) → UnitResult<E>` | string error ⇒ typed error | the migration path from `string` errors to `E` | |
| `MapError<T,E>(Result<T,E>, Func<E,E2>)` | typed ⇒ typed | | |
| `MapError<E>(UnitResult<E>, Func<E,string>) → Result` | typed ⇒ string | the reverse migration | |
| `MapIf<T>(Result<T>, bool condition, Func<T,T>) → Result<T>` | transform only if condition | conditional remap | condition depends on the value ⇒ use the `Func<T,bool>` overload |
| `MapTry<T,K>(Result<T>, Func<T,K>, Func<Exception,string>) → Result<K>` | `Map` + exception capture | mapping that can throw | mapping that can *legitimately fail* ⇒ `Bind` |
| `MapTry<K>(Result, Func<K>, Func<Exception,string>)` | | | |
| `MapTry<T,K,E>(Result<T,E>, Func<T,K>, Func<Exception,E>)` | typed variant | | |

> **`MapError` never changes `T`.** All four `MapError` rows above return the success type they
> were handed. So it is the right member for "same result, better reason" and the *wrong* member
> for a hand-rolled `Result.Failure<Other>(x.Error)`, which is a re-type. See §4 item 12.

#### Chaining

| member | semantics | use when | do not use when |
|---|---|---|---|
| `Bind<T,K>(Result<T>, Func<T,Result<K>>) → Result<K>` | monadic chain; stops at the first failure | **3+ dependent steps** — the flagship rewrite of a guard ladder | 1–2 links, or a hot path (the repo's own `ToolDispatcher.cs:200-202` rule) |
| `Bind<K>(Result, Func<Result<K>>)`, `Bind<T>(Result<T>, Func<T,Result>)`, `Bind(Result, Func<Result>)` | the rest of the arity matrix, incl. `UnitResult<E>` | | |
| `BindZip<T1,T2,K>(Result<(T1,T2)>, Func<T1,T2,Result<K>>) → Result<(T1,T2,K)>` | zip a tuple-valued result with a 2/3/…/7-ary function | destructuring a tuple result | |
| `BindIf` / `CheckIf` | chain only if a condition holds | | |
| `SelectMany` | `Bind`, aliased for LINQ | `from x in a from y in b select …` | |
| `Select` | `Map`, aliased for LINQ | `from x in a select …` | |

#### Validating mid-chain

| member | semantics | use when | do not use when |
|---|---|---|---|
| `Ensure<T>(Result<T>, Func<T,bool> predicate, string errorMessage) → Result<T>` | on success, if the predicate fails ⇒ failure; **value is lost** | a domain invariant on the happy path | you need to keep the value on violation — carry it in the message or use `Bind` |
| `Ensure<T,E>(Result<T,E>, Func<T,bool>, E error)` | typed variant | | |
| `Ensure<T>(Result<T>, Func<Result<T>> predicate)` | run another `Result` as a check | cross-field validation | |
| `EnsureNot<T>(Result<T>, Func<T,bool> test, string error)` | inverted predicate | "must be empty" | |
| `EnsureNotNull<T>(Result<T?>, string error) → Result<T>` | `T?` ⇒ `T`, failing when null | **this is the direct replacement for the nullable-`!` pattern** | the value was never nullable |
| `Ensure(Func<Result>)` / `Ensure<T>(Result<T>, Func<Result>)` | precondition check | | |

#### Observing without changing

| member | semantics | use when | do not use when |
|---|---|---|---|
| `Tap<T>(Result<T>, Action)` / `(Action<T>)` | side effect, returns the **same** `Result` | inserting a log/trace/telemetry into a chain | |
| `TapIf` / `TapError` / `TapErrorIf` | the conditional / failure-side variants | "log only on failure" | |
| `TapTry` / `TapIfTry` | side effect that can throw, converted to a failure | | |
| `OnSuccessTry(Result, Action[, errorHandler])` | side effect on success, throwing ⇒ failure | | |
| `OnFailureCompensate(Result, Func<Result>)` / `(Func<string,Result>)` | recovery attempt on failure | **retry / fallback** | |

#### Recovering

| member | semantics | use when | do not use when |
|---|---|---|---|
| `Compensate<T>(Result<T>, Func<string,Result<T>>) → Result<T>` | on failure, try to produce a *success* | **fallback: "if the key is missing, use the default"** | the fallback can itself fail — chain instead |
| `Compensate(Result, Func<string,Result>)`, and every `E`/`E2` arity | | | |
| `Check<T>(Result<T>, Func<T,Result>) → Result<T>` | a `Result`-returning check that **discards its value** but **keeps the original** | "run this validation, but return what I gave you" — the repo uses it (`TurnRunner`, `BackgroundDrain`) | you need the check's value ⇒ `Bind` |
| `CheckIf` | the conditional form | | |
| `BindTry<T,K>(Result<T>, Func<T,Result<K>>, Func<Exception,string>)` | chain with exception capture in the continuation | | |
| `BindWithTransactionScope` / `MapWithTransactionScope` / `WithTransactionScope` | wrap the continuation in a `TransactionScope` | storage writes | Harbor has 0 occurrences and no `System.Transactions` dependency — do not add |

#### Converting

| member | semantics | use when | do not use when |
|---|---|---|---|
| `ConvertFailure()` / `ConvertFailure<K>()` / `ConvertFailure<K,E>()` | **re-types the error of a failure; throws `InvalidOperationException` on success** | an `if (r.IsFailure) return Result.Failure<K>(r.Error);` ladder | it throws on success — guard it or use a `MapError` |
| `AsMaybe<T>(Result<T>) → Maybe<T>` | see above | | |

### 2.8 LINQ query syntax over `Maybe` / `Result` — supported, and under-used

`3.7.0` ships `Select` / `SelectMany` on both `Result` (via `in`-parameter extensions) and `Maybe`, in all four async shapes, **specifically to enable C# query syntax**. `version.txt` for the tag records `* #608 Improve linq query support` and `* #602 Add Tap extensions to Maybe`.

```csharp
// equivalent, both compile against 3.7.0
var c = name.Create("jsmith")
            .Bind(n => email.Create(n + "@x.com").Map(e => new Customer(n, e)));

var c =
    from n in name.Create("jsmith")      // SelectMany
    from e in email.Create(n + "@x.com")  // SelectMany
    select new Customer(n, e);            // Select
```

Async works the same, because the `Task`/`ValueTask` overloads exist:

```csharp
var billing = await (
    from customer in _repo.GetByIdAsync(id)                  // Task<Result<Customer>>
    from info in _gateway.ChargeAsync(customer, amount)      // Task<Result<BillingInfo>>
    select info);                                            // Result<BillingInfo>
```

**Use when:** 3+ dependent steps where the intermediate names are noise. This is the **only** form in the library that eliminates the intermediate-variable ladder *and* keeps the `Result` type visible, so it is the strongest candidate for the 149 `IsFailure` guards in the ROP domain.

**Do not use when:** 1–2 links. Query syntax has real costs — a closure allocation per `from`, an iterator state machine, and it hides `Bind` from grep. On a hot path, or when you need to read which combinator is in play, write the `Bind` chain. (This is the same rule the codebase already states in `ToolDispatcher.cs:200-202`.)

### 2.9 Value objects / entities

| type | purpose |
|---|---|
| `abstract class ValueObject` | structural equality via `protected abstract IEnumerable<object> GetEqualityComponents()`; caches hash; knows about EF Core / NHibernate proxies |
| `abstract class ValueObject<T> where T : ValueObject<T>` | the generic flavour; override `EqualsCore(T)` + `GetHashCodeCore()`. Upstream's own comment: *"Use non-generic ValueObject whenever possible"* |
| `abstract class ComparableValueObject` | adds `GetComparableEqualityComponents()` + `IComparable` |
| `abstract class SimpleValueObject<T> : ComparableValueObject where T : IComparable` | one-property value object, plus `implicit operator T` — the closest thing the library has to "primitive obsession" fix |
| `abstract class EnumValueObject<TEnumeration,TId>` | smart enum: `FromId(TId) → Maybe<TEnumeration>`, `FromName(string) → Maybe<TEnumeration>`, `All`, `Is(name)`, `Is(id)` |
| `abstract class Entity<TId>` / `Entity : Entity<long>` | identity equality by `Id`, `IsTransient()`, `IComparable` |
| `interface ICombine { ICombine Combine(ICombine value); }` | lets `Result.Combine<T,E>` fold a typed-error union without a lambda |

**Harbor usage: `ValueObject<` = 0, `EnumValueObject` = 0, `SimpleValueObject` = 0.** Harbor's identifiers (`src/Harbor.Abstractions.Contracts/Models/Identifiers/Identifiers.cs`) hand-roll the same idea. That is a legitimate finding for the sweepers, but note the library's value objects are `class` (reference type) and carry an `ISerializable` `[Serializable]` shape — swapping them in is a semantics change, not a drop-in.

### 2.10 Not applicable here

- **`CSharpFunctionalExtensions.HttpResults`** (`co-IT/CSharpFunctionalExtensions.HttpResults`) — web `HttpResponseMessage` helpers. Harbor is a CLI/TUI/desktop harness with no ASP.NET surface. Not applicable.
- **`CSharpFunctionalExtensions.FluentAssertions`** (`NitroDevs/…`) — assertion extensions. Harbor is **TUnit-only** (`AGENTS.md` §What NOT to do #5 bans xUnit/NUnit and mandates TUnit). Not applicable; do not add the package.

### 2.11 Not in this library at all

Verified against the tag's full file list and the DLL's metadata string heap:

- **`Either<L,R>`** — absent. `Result<T,E>` is the closest thing and it is not the same shape.
- **`Unit`** — absent. `UnitResult<E>` is the "no value" result.
- **`Error`** — absent. Errors are `string` or your own `E`.
- **`Maybe<T>.SelectMany` over `Task`** — present, all four shapes.

---

## 3. Ranked: the ~20 members most likely reinvented here, with the grep pattern that reveals each

**Baseline.** Measured on `origin/dev` @ `ca1b218` with
`git ls-files -- 'src/Harbor.Application' 'src/Harbor.Registries' 'src/Harbor.Tools.Builtin' 'src/Harbor.Storage.*' 'src/Harbor.Abstractions*' 'src/Harbor.Providers.OpenAiCompatible' 'src/Harbor.Terminal.Pty' | grep '\.cs$'`
(214 files) — the same ROP perimeter issue #571 used. The counts below reproduce #571's table (149/12/44/18/9/13/7/15) exactly, so the two measurements agree and the numbers are trustworthy as a shared baseline.

`DOM` = ROP-domain files (214). `NT` = all non-test `.cs` (1088; excludes `tests/`, `contrib/tests/`, `contrib/apps/`).

Run all of these with `rg` (or `grep -rnE`) over the perimeter, excluding `tests/`.

| # | library member | grep pattern (the search key) | DOM | NT | notes |
|---|---|---|---|---|---|
| 1 | **`Bind`** (guard ladder) | `if *\([^)]{0,120}\.IsFailure\s*\)` | **149** | 303 | 103 of the DOM sites are multi-line (`if (…)` then `return` on the next line); **39 DOM files hold ≥2 such guards, 148 sites in those 39 files**. A file with 2+ is a `Bind` chain written out longhand. Plus `if (x.IsSuccess)` 12 / 47. **Top target.** |
| 2 | **`Bind`** (second form) | `\.Bind\(` | 44 | 60 | the 44 that *did* get written — proof the team already knows the idiom. |
| 3 | **`Map`** (if/else remap) | `if *\([a-zA-Z_][a-zA-Z0-9_]*\.IsSuccess\)` | 12 | 45 | `if (r.IsSuccess) return Result.Success(f(r.Value)); return r;` |
| 4 | **`Map`** (ternary remap) | `\?\s*Result\.Success` | 21 | 21 | the ternary is the single most literal `Map` smell in the repo |
| 5 | **`Result.Try`** (try/catch) | `try\s*\{` + `catch *\(` inside a method returning `Result` | 13 | 87 | 676 `catch (` in DOM. `Result.Try(func, ex => ex.Message)` replaces the whole block. Already used 38× in DOM. |
| 6 | **`Result.SuccessIf` / `FailureIf`** | the *absence* is what to grep: `return \w+ \? Result\.Success\(` and `return \w+ \? Result\.Failure\(` | 3 | 3 | 3 uses vs 21 ternary-`Success` + 21 ternary-`Failure`. Nearly the whole idiom is unadopted. |
| 7 | **`Result.Of`** | `Result\.Of\(` → **0 hits anywhere**; the reinvented form is a hand-rolled async wrapper: `async` method that only `await`s and then `return Result.Success(` | **0** | 0 | issue #561 (`CompilationResult`) is the named instance: a hand-rolled `Result<T>` reimplemented twice. `Result.Of(value)` / `Of(() => …)` / `Of(task)` / `Of(() => task)` covers all four. |
| 8 | **`Maybe<T>`** (nullable lie) | `Result<[^>]*\?>` in a signature; `null!`; `\?\? *throw` | 17 / 2 / 6 | — | `Maybe<` itself is only 4 DOM / 20 NT. #562 (`HarborCompositionContext`, 5 `null!`) + #559 (`IAgent.State`). A `Maybe<T>` in the signature removes the `!` at the source instead of suppressing it. **#559 landed**: `IAgent.State` is `Maybe<AgentState>`, `DefaultAgent` keeps a plain `AgentState?` field and projects it (a struct field would lose the single-word atomic read the old auto-property had), and `tests/Harbor.Architecture.Tests/AgentStateContractRules.cs` guards the shape. Note for the next converter: `Maybe<T>` is a STRUCT, so `x is not { }` **always matches** — test `HasNoValue` / `HasValue`, never an empty property pattern. |
| 9 | **`EnsureNotNull`** | `Result<[^>]*\?>` | 17 | — | **0 uses.** This is the mechanical replacement for the `Result<T?>` signature plus its `!`. Highest value-per-line on this list. |
| 10 | **`Ensure`** (post-success invariant) | `if *\(.*\.IsSuccess\)` followed by a second `if` that returns a failure | 7 | 9 | post-success validation written as a second guard. `.Ensure(pred, "msg")` reads as one link. |
| 11 | **`MapError`** (error rewrite) | `Result\.Failure(<[^>]*>)?\((?s:[^;]|\n){0,400}?\$"[^"\n]*\{[a-z_][a-zA-Z0-9_]*\.Error\}` | 17 | 4 | **CORRECTED 2026-09-29 (was "40 / 40" via `Result\.Failure(<[^>]*>)?\([a-z_]\w*\.Error`).** The old regex matched the *bare* `Failure(x.Error)` spelling, which is a **re-type**, not an error rewrite — `MapError` is `Result<T> → Result<T>` and cannot change `T` (see §4 item 12). The hand-BUILT form is the real target: 17 sites, of which **4** are type-preserving and therefore convertible. The other 13 re-type (`Result<Session>` → `Result<SubAgentRunResult>`, `Result<TokenResponse>` → `Result<string>`, …) and belong to a `ConvertFailure`/re-type wave. Landed: 4 conversions + `tests/Harbor.Architecture.Tests/MapErrorFailureShapeTests.cs`. |
| 12 | **`Result.Combine`** | adjacent guard pairs, and any `foreach` over a collection of results that returns on the first failure | 2 | 3 | `Result.Combine` concatenates **all** error messages and **deduplicates with `×N`** (`"a (3×)"`); the manual ladders report one. 2 uses vs 149 guard sites. |
| 13 | **`CombineInOrder` / `Combine` over `Task`** | `Task\.WhenAll` | 8 | 8 | 8 `Task.WhenAll` in DOM (`ToolDispatcher.cs:106`, `ProviderRegistry.cs:187`, `BashTool.cs:145`, `WorkspaceInspector.cs:165`, `SkillUpdater.cs:305`, …), each followed by a hand-rolled join over the resolved results. `AsyncResultExtensionsLeftOperand.CombineInOrder(IEnumerable<Task<Result>>)` is the member. |
| 14 | **`Maybe<T>.HasValue` / `HasNoValue`** | `Has(No)?Value` | 8 | 46 | each is a one-step `Map`/`Execute`/`Or` written as a branch. |
| 15 | **`Maybe<T>.ToResult`** | a `Maybe`/null checked and then `return Result.Failure(` | 3 | 3 | "absent ⇒ failure" is exactly `ToResult(m, "msg")`. 3 uses; the `== null → Result.Failure` shape is 0 in DOM, so the *mistake* (a `Maybe` used for an error) is not common — the *gap* is the reverse (`Result` used for absence, see §4.6). |
| 16 | **`Maybe<T>.Or`** (the `??` ladder) | `GetValueOrDefault\([^)]*\)\s*\?\?` and `if *(.*\.HasNoValue\)[^\n]*\n?[^\n]*= ` | 1 | 1 | the repo does this right once (`ConfigNormalizer.cs:46-54`, `From → Where → Or → AsNullable`) and by hand elsewhere. |
| 17 | **`AsNullable`** | `AsNullable` | 1 | 1 | 1 use, in that one exemplary chain. The reinvented form is `m.HasValue ? m.Value : (T?)null`. |
| 18 | **`TryFirst` / `TryLast` / `TryFind`** | `FirstOrDefault\(` | 5 | 67 | 67 `FirstOrDefault` repo-wide (5 in DOM). `FirstOrDefault` returning `null` then a null-check is `TryFirst` / `TryFind` returning `Maybe`. |
| 19 | **`Tap` / `TapError`** (logging written as a branch) | `if *\(.*\.Is(Failure|Success)\)[^\n]*\n[^\n]*(_log\|logger\|Log\.)` | 13 / 16 | 13 / 16 | already the second-most-adopted member after `Bind`. Hunt the **unadopted** sites: a log call inside an `IsFailure` branch that mutates nothing and could have been `.TapError(…)`. |
| 20 | **`Match`** (terminal `if/else` at a boundary) | `if *\(.*\.IsSuccess\)` at the **end** of a method whose return type is **not** `Result` | 15 | 27 | 15 uses vs 12 `IsSuccess` guards still open. `Match` is the only form that lets both branches be `Action`/`Func` with no intermediate and no `return Result.Success(...)` wrapper. |

**Honest negative results** (worth stating so the sweepers do not waste a slice on them):

- **Zero or near-zero in the whole non-test tree:** `BindIf` 0, `BindTry` 0, `MapIf` 0, `MapTry` 2, `BindZip` 0, `EnsureNot` 0, `EnsureNotNull` 0, `ConvertFailure` 0, `Compensate` 4, `Check` 2, `CheckIf` 0, `SelectMany` 0, `Flatten` 0, `Optional` 0, `Deconstruct` 0, `Choose` 0, `ExecuteNoValue` 0, `TapNoValue` 0, `FirstFailureOrSuccess` 0, `WithTransactionScope` 0, `OnFailureCompensate` 0, `OnSuccessTry` 0, `Required` 0, `ToUnitResult` 0, `AsNullable` 1, `GetValueOrThrow` 0, `TryFind` 0, `TryFirst` 0, `TryLast` 0, `UnitResult` 0, `ValueObject<` 0, `Result.Of` **0**.
  A sweeper assigned "conditional combinators" (`BindIf`/`CheckIf`/`MapIf`/`EnsureNot`) will find **nothing**. A sweeper assigned "guard ladders / null handling / error rewrites" will find nearly everything. Split the work accordingly.
- `.Combine(` and `.Select(` return 84 / 42 in a naive grep over the whole tree — those are **LINQ**, not the library. Always scope the grep to files that `using CSharpFunctionalExtensions`. 199 files do (174 actually use the library types).
- `FirstOrDefault(` is 67 repo-wide but only **5** in the ROP domain. A sweeper assigned "collection lookups" and scoped to the domain will find 5 sites; scoped to the whole tree it finds 67, most of them unrelated to ROP.
- `Task.WhenAll` is **8** in the domain, and at least 3 of the 8 are already hand-rolled result-joins (`ToolDispatcher.cs:106`, `ProviderRegistry.cs:187`, `BashTool.cs:145`). The other 5 (`WorkspaceInspector.cs:165`, `SkillUpdater.cs:305`, …) are genuine stream drains, **not** `CombineInOrder` candidates. Do not report those.

**Reproducing every number in this table:**

```bash
DM=$(git ls-files -- src/Harbor.Application src/Harbor.Registries src/Harbor.Tools.Builtin \
        src/Harbor.Storage.Jsonl src/Harbor.Storage.Memory src/Harbor.Storage.Sqlite \
        src/Harbor.Abstractions src/Harbor.Abstractions.Contracts \
        src/Harbor.Providers.OpenAiCompatible src/Harbor.Terminal.Pty | grep '\.cs$')   # 214 files

grep -rhoE 'if *\([^)]{0,120}\.IsFailure\s*\)' --include='*.cs' $DM | wc -l            # 149
grep -rhoPzo 'if *\([^)]{0,120}\.Is(?:Failure|Success)\s*\)\s*\n\s*\{?\s*\n?\s*return ' \
     --include='*.cs' $DM 2>/dev/null | tr '\0' '\n' | grep -cE 'if *\('             # 103
grep -rhoE '\.Bind\(' --include='*.cs' $DM | wc -l                                    # 44
grep -rhoP 'Result\.Failure(<[^>]*>)?\([a-z_][a-zA-Z0-9_]*\.Error' --include='*.cs' $DM | wc -l   # 40
grep -rhoE 'Result<[^>]*\?>' --include='*.cs' $DM | wc -l                             # 17
grep -rhoE 'Task\.WhenAll' --include='*.cs' $DM | wc -l                                 # 8
grep -rhoE 'Result\.Of\(' --include='*.cs' $DM | wc -l                                  # 0
```

(the `\P`-based multi-line guard count needs GNU `grep -P`; on BSD/macOS use `rg -U`.)

### 3.1 What a guard ladder actually costs, in this repo

Three files already say the rule out loud, and #571 records it:

- `src/Harbor.Application/Agents/ToolDispatcher.cs:200-202` — guard ladder is the deliberate choice for 1–2 links and for hot paths.
- `src/Harbor.Application/Providers/…` / `HostBuilder.CliConfig.cs` / `PatchTool.cs` ("ROP-A Z1 п.3").
- `src/Harbor.Registries/…ProviderRegistry.cs:163`.

**The rule that follows from the surface above, and that the sweepers should measure against:** *guard ladder for 1–2 links and any hot path; `Bind`/`Map`/query-syntax from 3 links up.* That is not a stylistic preference — it is the point at which `Bind` actually saves a line rather than adding an indentation level, and it is the same threshold the existing comments already cite.

---

## 4. Library gotchas a reader must know before adopting

1. **`.Value` throws; `.Error` throws.** `Result<T>.Value` on failure throws `ResultFailureException(Error)`; `.Error` on success throws `ResultSuccessException`. `Maybe<T>.Value` on `None` throws `InvalidOperationException("Maybe has no value.")`. Nothing returns a sentinel. This is why the repo's `.Value` access is 100% guarded and why it must stay so.
2. **`Result.Failure<T>("")` throws `ArgumentNullException`, it does not produce an empty error.** `ErrorStateGuard` rejects a failure with a null/empty error and a success with a non-default error. Issue #561 (`CompilationResult.Error` returning `string.Empty`) is exactly this trap: an "empty error" is not a representable `Result`.
3. **`Failure` and `Failure<T>` are NOT interchangeable, and neither is a subtype of the other.** They are three unrelated structs (`Result`, `Result<T>`, `Result<T,E>`). The compiler *does* insert conversions — `Result<T>` ⇒ `Result` and `Result<T>` ⇒ `UnitResult<string>` are implicit — so `Result.Failure("x")` and `Result.Failure<T>("x")` will silently unify through `Result` in an overload set and change which member you picked. The lossy direction (`Result<T>` ⇒ `Result`) throws away the value, which is fine and often intended, but it means **a function that returns `Result` can silently swallow a `Result<T>`'s value**. Review any signature change from `Result<T>` to `Result`.
4. **`Result<T,E>` has an implicit conversion from `E` as well as from `T`.** `UnitResult<E> x = someError;` compiles. If `T` and `E` are both implicit-convertible from the same literal or the same `null`, the call is **ambiguous** — this is why upstream put `UnitResult` in a separate static class and calls it "an alternative entrypoint to avoid ambiguous calls". Do not add your own overloaded factories that take both a value and an error.
5. **Nothing is lazy except the delegates you pass.** `Bind`/`Map`/`Ensure` do not invoke their `Func` on the wrong branch, but they invoke it **eagerly on the right branch**. `Combine` and `CombineInOrder` are the exceptions: they are **eager and non-short-circuiting** — every element is evaluated, all error messages are joined, and duplicate messages are collapsed with an `×N` count (`"a (3×)"`). If you need fail-fast, that is a guard ladder, not `Combine`.
6. **`Result` used where `Maybe` is right** (the headline gotcha) loses the reason, so the caller cannot report it. **`Maybe` used where `Result` is right** loses the diagnostic too, and is worse because it is silent. The three-way decision:
   - can fail with a reason the user must see ⇒ `Result` / `Result<T>`
   - typed reason, and you have a type for it ⇒ `Result<T,E>` / `UnitResult<E>`
   - genuinely optional, absence is not an error ⇒ `Maybe<T>` (and if the *lookup itself* can fail, `Result<Maybe<T>>`)
7. **`Maybe<T>` never implicitly converts back to `T?`.** Only `T → Maybe<T>` and the `Maybe` marker → `None`. To go back: `AsNullable()`, `GetValueOrDefault()`, or `Or`.
8. **`GetValueOrDefault` exists twice, twice over.** `Result<T>.GetValueOrDefault(T)` is an **instance** method; `ResultExtensions.GetValueOrDefault(this Result<T>, Func<T>)` is an **extension** returning `T` from a `Result<T>` with a different signature; and `MaybeExtensions.GetValueOrDefault` is a third, on `Maybe<T>`. Same for `GetValueOrThrow` and `TryGetValue`. A grep for the name tells you nothing about which one a call site used.
9. **`TryGetValue`/`TryGetError` are annotated with `[NotNullWhen]`/`[MaybeNullWhen]`.** They are the *interop* escape hatch, deliberately kept out of new code (upstream's own comment on `Maybe<T>.Value`: *"Try to use GetValueOrThrow() or GetValueOrDefault() instead for better explicitness"*). Note `Result<T>.TryGetError(out string, out T)` returns `IsFailure` while setting **both** — the polarity of the return is the *opposite* of `TryGetValue`. Easy to invert.
10. **`Ensure` destroys the value on violation.** `r.Ensure(x => x.Count > 0, "empty")` returns a failure whose `Value` is gone. `EnsureNotNull` is the same idea, converting `Result<T?>` ⇒ `Result<T>`. If you need the value *and* the error, use `Bind`.
11. **`Compensate` is the only combinator that converts failure ⇒ success.** It is the closest thing to a fallback and it reads like a bug to anyone who has not met it. It is used 4× in this repo, in `AuthStore.cs:43-45` — where it is exactly right.
12. **`ConvertFailure` throws `InvalidOperationException` on success, and it is the only member that changes `T`.** It is not a safe re-type — it is only safe already inside a failure branch. And do not route re-types to `MapError`: `MapError` is `Result<T>` ⇒ `Result<T>`, and every 3.7.0 overload preserves `T` (the typed-error forms `Result<T,E>` → `Result<T,E2>` move the *error* type and leave the success type alone). Verified against the pinned source, not the XML doc — `MapError.cs:10` `MapError(this Result, Func<string,string>) → Result`, `MapError.cs:56` `MapError<T>(this Result<T>, Func<string,string>) → Result<T>`. The decision at a hand-built `Failure<T>("ctx: " + x.Error)` site is therefore mechanical: **same `T` on both sides ⇒ `MapError`; different `T` ⇒ a re-type, and `MapError` cannot express it.**
13. **`Result.Configuration` is global mutable static state** — `ErrorMessagesSeparator`, `DefaultConfigureAwait`, `DefaultTryErrorHandler`. Setting it in a library initializer affects the whole process. Harbor does not touch it; keep it that way.
14. **Every combinator has four async shapes and they are not free.** `Task`/`ValueTask` × receiver-task × selector-task. The internal `DefaultAwait` applies `Result.Configuration.DefaultConfigureAwait` (default `false`), so the library's own awaits do **not** capture a synchronization context. The repo's explicit `.ConfigureAwait(false)` inside `Bind`/`Map` lambdas is therefore redundant-but-harmless and is a matter of local convention, not a correctness requirement — do not report those as a finding.
15. **`Combine<T,E>` returns `Result<bool,E>`, and the `bool` is meaningless.** Upstream has three `// TODO: Ideally, we would be using BaseResult<E>` comments on those exact overloads. Never read that `bool`; if you need it, project to something real first.
16. **The upstream `master` README describes a newer API than we pin.** `Error`, `Maybe<Error>`, `UnitResult<Error>` exist on `master` and **do not exist in 3.7.0**. Copying an example from the master README into Harbor will not compile.

---

## 5. Analyzers (CFE0001 ADOPTED, pinned to 1.3.0 — measured)

Upstream publishes a separate Roslyn analyzer package: **`CSharpFunctionalExtensions.Analyzers`**
([repo](https://github.com/AlmarAubel/CSharpFunctionalExtensions.Analyzers), [NuGet](https://www.nuget.org/packages/CSharpFunctionalExtensions.Analyzers)).
It is a `netstandard2.0` `DevelopmentDependency` analyzer assembly (`analyzers/dotnet/cs`), and it is a **separate repository from the main library** — it is not part of the `3.7.0` package.

It ships **two** diagnostics:

| id | title | severity | what it catches | how |
|---|---|---|---|---|
| **`CFE0001`** | *"Check IsSuccess or IsFailure before accessing Value from result object"* | Warning, enabled by default | `result.Value` where the enclosing function body never checks `IsSuccess`/`IsFailure` first | `RegisterSyntaxNodeAction(…, SimpleMemberAccessExpression)`; matches `memberAccess.Name == "Value"` on an `IPropertySymbol` whose containing type is `Result` in namespace `CSharpFunctionalExtensions`, then walks the function body for a check |
| **`CFE0002`** | *"Prefer Implicit Type Arguments for Result Methods"* | Info, **disabled by default** | `Result.Success<T>(x)` / `Result.Failure<T>(e)` where the target type already determines `T` — i.e. `return Result.Failure<Foo>("…")` in a `Foo`-returning method | `RegisterSyntaxNodeAction(…, InvocationExpression)`; inspects the return statement's or variable declaration's target type, with a code fix that drops the explicit type argument |

**Status: `CFE0001` is adopted** (owner override of the no-new-packages rule, for this package only),
solution-wide via `Directory.Build.props`, `PrivateAssets="all"`. **`CFE0002` stays off** — it is
`Info`, disabled upstream, and low value here: Harbor's `Result.Failure<T>(…)` sites are mostly not in
an inferable position.

### 5.1 The version is NOT the library's version — pin 1.3.0, not 3.7.0

The analyzer is a **separate repository with its own GitVersion scheme**. There is no `3.7.0` release
of `CSharpFunctionalExtensions.Analyzers`; pinning it "to match" the library fails restore with
NU1102. Latest stable is **1.3.0** (see `Directory.Packages.props`).

**1.4.x must not be used.** 1.4.1 crashes the compiler with
`AD0001 … System.InvalidOperationException: Operation is not valid due to the current state of the
object` on five production projects (`Harbor.Application` ×40, `Harbor.Tools.Builtin` ×26,
`Harbor.Lsp` ×6, `Harbor.Ui.Framework.Sessions` ×4, `Harbor.Storage.Jsonl` ×4). Those projects then
**fail to compile**, so they emit no CFE0001 diagnostics at all — the guard looks clean precisely
where the risk is highest. 1.3.0 predates the `ResultValueWalker` rework (changelog: 1.4.1
"addressed multiple issues related to pattern matching and complex conditional logic") and runs clean.

### 5.2 Measured baseline (CI, `dotnet build Harbor.slnx -c Release`, analyzer 1.3.0)

**210 CFE0001 sites total: 36 in shipped code (src/ + apps/), 174 under `tests/`.**

| area | sites | verdict |
|---|---|---|
| shipped — real defect, fixed | 1 | **fixed** (see 5.3) |
| shipped — real defect, baselined | 1 | `SettingsViewModel` ctor; needs a product decision (5.3) |
| shipped — false positives | 34 | baselined, one documented pragma each (5.4) |
| `tests/` | 174 | all false positives of ONE shape; suppressed centrally (5.5) |

Per project: `Harbor.Lsp` 8, `Harbor.Application` 7, `Harbor.Desktop.Abstractions` 3,
`Harbor.Hosting` 3, `Harbor.Storage.Jsonl` 2, `Harbor.App.Cli` 6, `Harbor.Plugins.Hosting` 1,
`Harbor.Plugins.Runtime` 1, `Harbor.App.Avalonia` 3, `Harbor.Terminal.Abstractions.Tests` 1 (pre-existing break, see 5.9).

**A partial build reports a LOWER BOUND, never the count.** The shipped-code sites were found across
**six** successive runs: each run compiles further before failing, so each surfaced projects the
previous one never reached. The first 1.3.0 run reported 18 and looked complete; the real figure was
36. Re-measure until two consecutive runs agree — and note the first run to reach zero new sites is
not necessarily the last to reach zero unanalysed projects.

### 5.3 The one real defect: `HarborConfig.EffectiveModel`

```csharp
public string EffectiveModel => Identity.EffectiveModel().Value.ToString();
```

`IdentityConfig.EffectiveModel()` returns `Result<ModelRef>` and ends in `ModelRef.TryParse`, so it
**can fail**. This is an expression-bodied property on the startup path
(`HarborComposeOptions` → `ToolsCatalog` → `ReplRunner`), so a failure threw
`ResultFailureException` **out of a property getter during composition-root setup** — the §ROP-002
crash class, live. Now `GetValueOrDefault(IdentityConfig.Default.Model!)`, which applies the fallback
the property's own summary always promised. This is the only unguarded `.Value` the analyzer found in
shipped code, and the prior "35/35 clean" audit had missed it.

### 5.4 The 24 production false positives, by shape

The analyzer's guard detection is a heuristic function-body walk. It models `if`-scoped checks,
ternaries and switch arms; it does **not** model these, all of which Harbor uses constantly:

| shape | example | count |
|---|---|---|
| early `return` guard | `if (r.IsFailure) return; … r.Value` | 15 |
| early `continue` guard | `if (r.IsFailure) { …; continue; } … r.Value` | 4 |
| fail-fast `throw` guard | `if (psk.IsFailure) throw; … psk.Value` | 1 |
| `&&` short-circuit | `r.IsSuccess && … r.Value` | 1 |
| ternary mis-attribution | `built.IsFailure ? …err : …built.Value` | 1 |
| Result-shaped wrapper pass-through | `public T Value => _result.Value;` | 1 |
| bare assertion in a test | `Assert.That(r.IsSuccess).IsTrue(); Assert.That(r.Value)` | 174 (tests) |

Each baselined site carries a line-scoped `#pragma warning disable CFE0001` with its reason, so a
**new** `.Value` in a different member of the same file is still a build error. The table of rows
lives in `tests/Harbor.Architecture.Tests/CfeValueBaselineTests.cs`, and is keyed by
**file + member** (not line) because inserting a pragma shifts every line below it.

### 5.5 Tests: suppressed centrally, not with 174 pragmas

`tests/` is exempted in the same `Directory.Build.props` `PropertyGroup` that already exempts
`RS0030`. In a test the thrown exception **is** the failure signal —
`(await store.CreateAsync(…)).Value` throwing means the fixture broke, which is what the test should
do. Production is not covered by that condition: `src`/`apps`/`contrib` keep CFE0001 at full severity.

### 5.6 What this cannot see — read a green CFE0001 as evidence, not proof

- **A `.Value` read across an `await` is invisible — and that is the shape async code always uses.**
  CFE0001 registers on `SimpleMemberAccessExpression` and resolves the **receiver** to an
  `IPropertySymbol` on `CSharpFunctionalExtensions.Result`. For

  ```csharp
  var config = (await _configStore.LoadAsync().ConfigureAwait(false)).Value;
  ```

  the receiver is a parenthesized `AwaitExpression`, which has no symbol to resolve, so the node is
  skipped. The synchronous spelling of the same hole is
  `store.LoadAsync().GetAwaiter().GetResult().Value`, where the receiver is an invocation. Both
  spellings were live defects (#602): a hand-edited `~/.harbor/config.json` with a trailing comma
  makes `JsonConfigStore` return `Failure("config.json is corrupt: …")`, and the unguarded read
  turned that into a `ResultFailureException` out of `harbor ask` and out of the interactive path.
  `tests/Harbor.Architecture.Tests/UnguardedResultReadRules.cs` is the companion backstop for exactly
  those two spellings, plus `GetAllAgents()[0]` — not a `Result` site at all, so no analyzer can
  ever see it. The §5.2 counts are unaffected by #602: the sites it fixed were never counted,
  because the analyzer never saw them.
- **Hand-rolled `Result<T>` types are structurally invisible.** CFE0001 matches on the
  `CSharpFunctionalExtensions.Result` *symbol*. The defective shape in #561 —
  `IPluginCompiler.CompilationResult.Value => _assembly ?? throw …` with
  `Error => _error ?? string.Empty` — is invisible to it, as is #588's `ModelBatch`. **CFE0001 says
  nothing about those issues.** (Note `PluginCompilationResult` *is* flagged, but it is the
  **correct** wrapper per #561; its `Value` is a documented pass-through guarded by its own
  `IsSuccess` contract.)
- It only inspects `MethodDeclarationSyntax` bodies: a `.Value` in a constructor, a local function or
  an expression-bodied member is not modelled.
- It ignores `?.Value`.
- It matches by property name on a `Result`-named type, not by the `IResult` interface.

### 5.7 Non-vacuity (the part that is easy to get wrong)

A guard that cannot fire is worse than no guard — the same lesson as NetArchTest treating a
non-existent assembly name as a satisfied constraint. Three mechanisms guard it:

1. `CfeControlPositiveControlTests` — **behaviour**. `analyzers/CfeControl/` is a throwaway project
   deliberately **not in `Harbor.slnx`** holding a known-bad unguarded read and a known-good guarded
   one. The test shells out to `dotnet build` and requires CFE0001 to fire on the first and stay
   silent on the second: sensitivity *and* specificity, because an analyzer that flagged every
   `.Value` would pass a sensitivity-only control while being useless. It lives outside `tests/`
   because `tests/Directory.Build.props` NoWarns CFE0001 — a control placed there would be silenced by
   the very suppression it polices.
2. `CfeValueBaselineTests` — **wiring**: the package is referenced and pinned; no *unconditional*
   `NoWarn` contains CFE0001; `.editorconfig` never sets it to `none`/`silent`/`suggestion`; the
   tests/samples condition has not widened to cover `src/`.
3. `CfeValueBaselineTests` — **honesty**: every baselined member still exists and still carries its
   pragma (so the baseline cannot rot into a blanket permission), and the counts are pinned.

### 5.8 A pre-existing break this work had to clear

Landing the analyzer surfaced, and this PR fixes, an **unrelated** break that was already red on
`dev@ce46c8d`: `tests/Harbor.Terminal.Abstractions.Tests/ViewRegistryFreezeTests.cs` still overrode
`ReadLineAsync` as `Task<Result<string>>` after `ITuiRenderer` changed that member to
`Task<Maybe<string>>` (CS0508). One word. It is in its own commit so it can be dropped independently —
but note it means **`dev` was red before this PR**, and any CI measurement taken against it is
against a tree that did not compile.

### 5.9 Cost, stated honestly

A new build-time dependency, and the repo's second Roslyn-related package after
`BannedApiAnalyzers` (Sonar, Roslynator, Meziantou, NetAnalyzers, AsyncFixer and Reflection are all
Roslyn analyzers too, so "second" understates it). It ships one useful rule with a 4% true-positive
rate (1 of 25), heavy false-positive shape coverage, and blind spots that include the exact
hand-rolled-`Result` bugs issues #561 and #588 are about. **Verdict: worth it as a cheap net for the
CFE-`Result` case, explicitly not as a §ROP-001 guarantee** — the audit and review convention remain
the primary defence, and §5.6's blind spots are the reason.

---

## 6. How to re-derive this document

The pinned version is the single source of truth. If `Directory.Packages.props:167` changes, this document is stale and must be regenerated:

```bash
# 1. the pinned version
grep -n 'CSharpFunctionalExtensions' Directory.Packages.props

# 2. source A — the shipped XML doc (names only, no return types)
ls ~/.nuget/packages/csharpfunctionalextensions/<VER>/lib/net8.0/

# 3. source B — the matching tag (exact signatures, semantics)
curl -sSL https://codeload.github.com/vkhorikov/CSharpFunctionalExtensions/tar.gz/refs/tags/v<VER> | tar xz
cat CSharpFunctionalExtensions/version.txt     # must equal <VER>

# 4. the baseline counts in §3
git ls-files -- 'src/Harbor.Application' 'src/Harbor.Registries' 'src/Harbor.Tools.Builtin' \
                'src/Harbor.Storage.*' 'src/Harbor.Abstractions*' \
                'src/Harbor.Providers.OpenAiCompatible' 'src/Harbor.Terminal.Pty' | grep '\.cs$'
```

Never take a number, a signature, or a member list from the upstream README. It is a curated example set on a moving branch and it is demonstrably incomplete (§1).
