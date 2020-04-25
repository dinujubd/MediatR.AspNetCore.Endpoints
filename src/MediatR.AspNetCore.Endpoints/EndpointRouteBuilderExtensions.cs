﻿using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MediatR.AspNetCore.Endpoints
{

    public static class EndpointRouteBuilderExtensions
    {
        public static void MapMediatR(this IEndpointRouteBuilder endpointsBuilder, string pathString)
        {
            endpointsBuilder.MapMediatR(new PathString(pathString));
        }

        public static void MapMediatR(this IEndpointRouteBuilder endpointsBuilder)
        {
            endpointsBuilder.MapMediatR(PathString.Empty);
        }

        public static void MapMediatR(this IEndpointRouteBuilder endpointsBuilder, PathString pathString)
        {
            var mediator = endpointsBuilder.ServiceProvider.GetService<IMediator>();
            if (mediator == null)
            {
                throw new InvalidOperationException($"IMediator has not added to IServiceCollection. You can add it with services.AddMediatR(...);");
            }

            var options = endpointsBuilder.ServiceProvider.GetService<IOptions<MediatorEndpointOptions>>();

            foreach (var handlerType in options.Value.HandlerTypes)
            {
                var requestHandlerType = handlerType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>));

                if (requestHandlerType == null)
                {
                    throw new InvalidOperationException($"Type ({handlerType.FullName}) is not an IReqeustHandler<,>" +
                        $"All types in {nameof(MediatorEndpointOptions)}.{nameof(MediatorEndpointOptions.HandlerTypes)} must implement IReqeustHandler<,>.");
                }

                var requestArguments = requestHandlerType.GetGenericArguments();
                var requestType = requestArguments[0];
                var responseType = requestArguments[1];

                var requestMetadata = new MediatorEndpointMetadata(requestType, responseType);

                var metadata = handlerType.GetMethod("Handle").GetCustomAttributes(false);

                var httpAttributes = metadata.OfType<HttpMethodAttribute>().ToArray();
                if (httpAttributes.Length == 0)
                {
                    var httpMethodMetadata = new HttpMethodMetadata(new[] { HttpMethods.Post });
                    CreateEndpoint(endpointsBuilder, requestMetadata, metadata, requestMetadata.RequestType.Name, pathString, httpMethodMetadata);
                }
                else
                {
                    for (int i = 0; i < httpAttributes.Length; i++)
                    {
                        var httpAttribute = httpAttributes[i];
                        var httpMethodMetadata = new HttpMethodMetadata(httpAttribute.HttpMethods);

                        string template;
                        if (string.IsNullOrEmpty(httpAttribute.Template))
                        {
                            template = "/" + requestType.Name;
                        }
                        else
                        {
                            template = httpAttribute.Template;
                        }

                        CreateEndpoint(endpointsBuilder, requestMetadata, metadata, template, pathString, httpMethodMetadata);
                    }
                }
            }
        }

        private static void CreateEndpoint(IEndpointRouteBuilder endpointsBuilder,
            MediatorEndpointMetadata requestMetadata,
            object[] metadata,
            string template,
            PathString pathString,
            HttpMethodMetadata httpMethodMetadata)
        {
            if (pathString.HasValue)
            {
                template = $"{pathString.Value.TrimEnd('/')}/{template.TrimStart('/')}";
            }

            var routePattern = RoutePatternFactory.Parse(template);

            var builder = endpointsBuilder.Map(routePattern, MediatorRequestDelegate);
            builder.WithDisplayName(requestMetadata.RequestType.Name);
            builder.WithMetadata(requestMetadata);
            builder.WithMetadata(httpMethodMetadata);

            for (int i = 0; i < metadata.Length; i++)
            {
                builder.WithMetadata(metadata[i]);
            }
        }

        private static async Task MediatorRequestDelegate(HttpContext context)
        {
            var endpoint = context.GetEndpoint();

            var requestMetadata = endpoint.Metadata.GetMetadata<IMediatorEndpointMetadata>();

            object model;
            if (context.Request.ContentLength.GetValueOrDefault() != 0)
            {
                //https://github.com/aspnet/AspNetCore/blob/ec8304ae85d5a94cf3cd5efc5f89b986bc4eafd2/src/Mvc/Mvc.Core/src/Formatters/SystemTextJsonInputFormatter.cs#L72-L98
                try
                {
                    model = await JsonSerializer.DeserializeAsync(context.Request.Body, requestMetadata.RequestType, null, context.RequestAborted);
                }
                catch (JsonException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                catch (Exception exception) when (exception is FormatException || exception is OverflowException)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
            }
            else
            {
                model = Activator.CreateInstance(requestMetadata.RequestType);
            }

            if (model is IHttpContextAware httpContextAware)
            {
                httpContextAware.HttpContext = context;
            }

            IMediator mediator = context.RequestServices.GetService<IMediator>();

            var response = await mediator.Send(model, context.RequestAborted);

            context.Response.Headers.Add("content-type", "application/json");

            var objectType = response?.GetType() ?? requestMetadata.ResponseType;
            await JsonSerializer.SerializeAsync(context.Response.Body, response, objectType, null, context.RequestAborted);

            await context.Response.Body.FlushAsync(context.RequestAborted);
        }
    }
}