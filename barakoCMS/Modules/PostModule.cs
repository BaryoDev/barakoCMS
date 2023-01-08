using barakoCMS.Requests;
using Carter;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace barakoCMS.Modules {

	public class PostModule : CarterModule {

		public PostModule() : base("/api/Posts") {
			this.RequireAuthorization();
			}

		public override void AddRoutes(IEndpointRouteBuilder app) {
			app.MapGet("/", async (IMediator _mediator, [FromQuery] int pageNumber, [FromQuery] int pageSize) => {
				try {
					var result = await _mediator.Send(new GetAllPostsRequest { PageNumber = pageNumber, PageSize = pageSize });
					return Results.Ok(result);
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			});

			app.MapGet("/{id}", async (IMediator _mediator, Guid id) => {
				try {
					var result = await _mediator.Send(new GetPostByIdRequest { Id = id });
					return Results.Ok(result);
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			});

			app.MapPost("/", async (IMediator _mediator, ClaimsPrincipal user, [FromBody] CreatePostRequest request) => {
				try {
					request.AuthorId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
					var result = await _mediator.Send(request);
					return Results.Created($"/api/Posts/{result.Post.Id}", result);
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			});

			app.MapPut("/", async (IMediator _mediator, ClaimsPrincipal user, Guid id, [FromBody] UpdatePostRequest request) => {
				try {
					request.Id = id;
					request.UserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
					var result = await _mediator.Send(request);
					return Results.Ok(result);
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			});

			app.MapDelete("/", async (IMediator _mediator, ClaimsPrincipal user, Guid id) => {
				try {
					var request = new DeletePostRequest { Id = id, UserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty };
					var result = await _mediator.Send(request);
					if (result.Success) {
						return Results.NoContent();
						}

					return Results.NotFound();
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			});
			}
		}
	}