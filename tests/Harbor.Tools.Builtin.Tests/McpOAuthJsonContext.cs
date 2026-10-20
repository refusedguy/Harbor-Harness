// McpOAuthJsonContext.cs — the source-generated context for the OAuth
// responses the MCP OAuth tests stub (#1822).
//
// WHY IT IS PROJECT-LOCAL
// -----------------------
// The payloads are OAuth wire documents — a token response, an error
// response, an authorization-server metadata document — that only this
// project fakes. They belong to the protocol this project speaks, not to the
// shared kit's tool-argument family.
//
// THE SHAPES ARE SEPARATE TYPES, NOT ONE BAG
// ------------------------------------------
// The reflection calls built four different documents, and three of them
// share no member: `{"error":"..."}` and `{"access_token":...}` are not the
// same shape with an empty member. One bag record with six optional members
// would write the same bytes, but it would also let a test build
// `{"access_token":"x","error":"y"}` — a document no OAuth server emits.
// Separate records keep the shapes apart.
//
// Every member carries its `[JsonPropertyName]`, so the bytes do not depend
// on a naming policy, and the members that the original documents omitted
// stay omitted: the token response WITHOUT `refresh_token` must not grow one.

using System.Text.Json.Serialization;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>     An empty OAuth response body. </summary>
public sealed record McpOAuthEmptyResponse();

/// <summary>     A token response that carries a refresh token. </summary>
public sealed record McpOAuthTokenWithRefreshResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>     A token response without a refresh token. </summary>
public sealed record McpOAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>     An OAuth error response. </summary>
public sealed record McpOAuthErrorResponse(
    [property: JsonPropertyName("error")] string Error);

/// <summary>     An authorization-server metadata document. </summary>
public sealed record McpOAuthServerMetadataResponse(
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint);

/// <summary>
///     Source-generated metadata for the OAuth responses this project stubs.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(McpOAuthEmptyResponse), TypeInfoPropertyName = "McpOAuthEmptyResponse")]
[JsonSerializable(typeof(McpOAuthTokenWithRefreshResponse), TypeInfoPropertyName = "McpOAuthTokenWithRefreshResponse")]
[JsonSerializable(typeof(McpOAuthTokenResponse), TypeInfoPropertyName = "McpOAuthTokenResponse")]
[JsonSerializable(typeof(McpOAuthErrorResponse), TypeInfoPropertyName = "McpOAuthErrorResponse")]
[JsonSerializable(typeof(McpOAuthServerMetadataResponse), TypeInfoPropertyName = "McpOAuthServerMetadataResponse")]
internal sealed partial class McpOAuthJsonContext : JsonSerializerContext
{
}
