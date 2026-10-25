using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Quacktion.Web.Infrastructure;

/// <summary>Expose the authentication required by the API to Swagger and generated clients.</summary>
public sealed class BearerSecurityDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        foreach (var api in context.ApiDescriptions)
        {
            var path = "/" + api.RelativePath?.Split('?')[0];
            if (api.HttpMethod is null || !document.Paths.TryGetValue(path, out var item)
                || item.Operations is null || !item.Operations.TryGetValue(new HttpMethod(api.HttpMethod), out var operation)) continue;
            var metadata = api.ActionDescriptor.EndpointMetadata;
            var protectedEndpoint = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
            // This action performs its own Bearer-or-Device authentication before creating a session.
            if (path == "/api/sessions/start") protectedEndpoint = true;
            if (!protectedEndpoint) continue;
            operation.Security = [new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            }];
        }
    }
}
