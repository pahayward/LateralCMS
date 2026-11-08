using Lateral.Cms.Assets;
using Lateral.Cms.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Lateral.Cms.ApiDocs;

/// <summary>
/// Swagger: an OpenAPI document generated from the routes the service really maps, and Swagger UI to read and try it. The
/// document is at <c>/swagger/v1/swagger.json</c>, the page at <c>/swagger</c>. Both need a caller signed in (the browser asks
/// for a username and password), and the page is served under its own, still strict, content security policy.
/// </summary>
public static class ApiDocsExtensions
{
    public const string UiPath = "/swagger";
    public const string DocumentPath = "/swagger/v1/swagger.json";
    public const string BasicScheme = "Basic";

    // Swagger UI loads its own scripts and styles from the same address and draws with inline styles; nothing else is allowed.
    internal const string UiPolicy = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "font-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    public static IServiceCollection AddCmsApiDocs(this IServiceCollection services)
    {
        services.AddOptions<ApiDocsOptions>().BindConfiguration(ApiDocsOptions.SectionName);
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Lateral CMS",
                Version = "v1",
                Description = "A webhook in, a read API out, and a store that always ends up in the right state.\n\n"
                    + "Every route needs a signed-in caller except health and sign-in. Press Authorize and enter a username and password (Basic) to try routes from this page. "
                    + "(A session cookie alone is not enough for calls that change something: those must also send an X-Requested-With header, which this page does not add.) "
                    + "Each route says who may call it. Nothing here changes a record's content except events sent by the CMS.",
            });
            options.AddSecurityDefinition(BasicScheme, new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "basic",
                Description = "A person's username and password, or the CMS service account's.",
            });
            options.OperationFilter<ApiDocsOperationFilter>();
            options.DocumentFilter<ApiDocsTagOrder>();
            options.SupportNonNullableReferenceTypes();
        });
        return services;
    }

    /// <summary>Serves the document and the page, after authentication, so that only a signed-in caller gets either.</summary>
    public static WebApplication UseCmsApiDocs(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<IOptions<ApiDocsOptions>>().Value.Enabled)
        {
            return app;
        }

        // The page and the document are not endpoints, but the fallback authorization policy (signed in, or 401 and a password box)
        // still covers them, because UseAuthorization has already run: a caller who is not signed in never reaches this point. The
        // tests ask for the page, its scripts and the document without credentials, so a change there is noticed. What is added
        // here is only the page's own content policy, and only under /swagger.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(UiPath))
            {
                context.Response.Headers["Content-Security-Policy"] = UiPolicy;
            }

            await next();
        });

        app.UseSwagger();
        app.UseSwaggerUI(ui =>
        {
            ui.RoutePrefix = UiPath.TrimStart('/');
            ui.SwaggerEndpoint(DocumentPath, "Lateral CMS v1");
            ui.DocumentTitle = "Lateral CMS API";
            ui.DisplayRequestDuration();
            ui.DefaultModelsExpandDepth(-1);
            // No request interceptor: Swagger UI would compile it with new Function, which the page's policy rightly forbids.
            // Calls made here carry Basic credentials (Authorize), which need no cross-site proof; a session cookie alone does.
        });
        return app;
    }
}

/// <summary>Lists the groups in the order a reader needs them (the CMS first), each with a line saying what it is for.</summary>
internal sealed class ApiDocsTagOrder : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context) => document.Tags =
    [
        new OpenApiTag { Name = "CMS: events", Description = "How the CMS gets content in, and how it follows a batch. CMS service account." },
        new OpenApiTag { Name = "CMS: files", Description = "How the CMS uploads files: one request, or a multipart upload in parallel parts. CMS service account." },
        new OpenApiTag { Name = "Records", Description = "Reading records. Nothing here can change a record's content." },
        new OpenApiTag { Name = "Files", Description = "Downloading and deleting files. Only clean files exist for users." },
        new OpenApiTag { Name = "Sign-in", Description = "Starting and ending a session, and your own password." },
        new OpenApiTag { Name = "Admin: people", Description = "Who can sign in. Admins only." },
        new OpenApiTag { Name = "Admin: running it", Description = "Settings, storage, the batch queue and the scanner queue. Admins only." },
        new OpenApiTag { Name = "Health", Description = "For a reverse proxy or monitor." },
    ];
}

/// <summary>Fills in what the routes alone do not say: a title, who may call, the statuses, the tag, and the bodies read by hand.</summary>
internal sealed class ApiDocsOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var name = metadata.OfType<IEndpointNameMetadata>().FirstOrDefault()?.EndpointName;
        var path = "/" + (context.ApiDescription.RelativePath ?? string.Empty).Trim('/');
        var isHead = string.Equals(context.ApiDescription.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

        operation.Tags = [new OpenApiTag { Name = TagFor(path) }];
        if (name is not null)
        {
            operation.OperationId = isHead ? name + "Head" : name;
        }

        var who = WhoMayCall(metadata);
        var anonymous = metadata.OfType<IAllowAnonymous>().Any();
        if (name is not null && RouteDescriptions.ByName.TryGetValue(name, out var description))
        {
            operation.Summary = isHead ? description.Summary + " (headers only)" : description.Summary;
            operation.Description = $"{description.Detail}\n\n**Who may call:** {who}";
            // The reflection adds a 200 for any handler that returns a result; where the real success is something else (a 202, a 204), that default is wrong.
            if (description.Statuses.All(s => s.Status != StatusCodes.Status200OK))
            {
                operation.Responses.Remove("200");
            }

            foreach (var (status, meaning) in description.Statuses)
            {
                operation.Responses[status.ToString()] = new OpenApiResponse { Description = meaning };
            }
        }

        if (!anonymous)
        {
            operation.Security = [new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiDocsExtensions.BasicScheme } }] = [] }];
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Not signed in, or the username or password is wrong." });
            operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Signed in but not allowed here, or a new password must be chosen first." });
        }

        // Bodies the handlers read from the stream themselves, so the reflection cannot see them.
        switch (name)
        {
            case "IngestCmsEvents":
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "A JSON array of events. publish and unPublish carry a version and a payload object; delete carries neither.",
                    Content =
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema { Type = "array", Items = new OpenApiSchema { Type = "object" } },
                            Example = EventExample(),
                        },
                    },
                };
                break;
            case "PutAsset":
            case "PutPart":
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Description = "The file's bytes, as is.",
                    Content = { ["application/octet-stream"] = new OpenApiMediaType { Schema = new OpenApiSchema { Type = "string", Format = "binary" } } },
                };
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = AssetEndpoints.Sha256Header,
                    In = ParameterLocation.Header,
                    Required = false,
                    Description = "The bytes' SHA-256 in hex. If sent, the upload is refused when it does not match.",
                    Schema = new OpenApiSchema { Type = "string" },
                });
                break;
        }
    }

    private static string TagFor(string path) => path switch
    {
        _ when path.StartsWith("/cms/events", StringComparison.Ordinal) || path.StartsWith("/cms/batches", StringComparison.Ordinal) => "CMS: events",
        _ when path.StartsWith("/cms/assets", StringComparison.Ordinal) => "CMS: files",
        _ when path.StartsWith("/api/entities", StringComparison.Ordinal) => "Records",
        _ when path.StartsWith("/api/assets", StringComparison.Ordinal) => "Files",
        _ when path.StartsWith("/api/auth", StringComparison.Ordinal) => "Sign-in",
        _ when path.StartsWith("/api/admin/users", StringComparison.Ordinal) => "Admin: people",
        _ when path.StartsWith("/api/admin", StringComparison.Ordinal) => "Admin: running it",
        _ => "Health",
    };

    private static string WhoMayCall(IList<object> metadata)
    {
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            return "anyone, no sign-in needed.";
        }

        return metadata.OfType<IAuthorizeData>().Select(a => a.Policy).LastOrDefault(p => p is not null) switch
        {
            Policies.CmsIngest => "the CMS service account only.",
            Policies.ApiRead => "a signed-in user or admin.",
            Policies.AdminOnly => "an admin only.",
            Policies.BatchStatus => "the CMS service account or an admin.",
            _ => "any signed-in caller.",
        };
    }

    private static IOpenApiAny EventExample() => new OpenApiArray
    {
        new OpenApiObject
        {
            ["type"] = new OpenApiString("publish"),
            ["id"] = new OpenApiString("article-1"),
            ["version"] = new OpenApiInteger(1),
            ["timestamp"] = new OpenApiString("2026-11-08T09:00:00Z"),
            ["payload"] = new OpenApiObject { ["title"] = new OpenApiString("Hello") },
        },
        new OpenApiObject
        {
            ["type"] = new OpenApiString("delete"),
            ["id"] = new OpenApiString("article-0"),
            ["timestamp"] = new OpenApiString("2026-11-08T09:00:05Z"),
        },
    };
}
