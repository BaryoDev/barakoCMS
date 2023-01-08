using barakoCMS.Requests;
using Carter;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace barakoCMS.Modules {

	public class AuthModule : CarterModule {

		public AuthModule() : base("api/auth") {
			}

		public override void AddRoutes(IEndpointRouteBuilder app) {
			app.MapPost("signin", async (IMediator _mediator, [FromBody] SignInRequest request) => {
				try {
					var result = await _mediator.Send(request);
					return Results.Ok(result);
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			})
				.Accepts<SignInRequest>(contentType: "application/json")
				.Produces<SignInResponse>(contentType: "application/json", statusCode: StatusCodes.Status200OK)
				.Produces(StatusCodes.Status400BadRequest);

			app.MapPost("signup", async (IMediator _mediator, [FromBody] SignUpRequest request) => {
				try {
					var result = await _mediator.Send(request);
					return result.Result.Succeeded ? Results.Ok() : Results.BadRequest(result.Result.Errors);
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			});

			app.MapPost("changepassword", async (IMediator _mediator, [FromBody] ChangePasswordRequest request) => {
				try {
					var result = await _mediator.Send(request);
					return result.Result.Succeeded ? Results.Ok() : Results.BadRequest(result.Result.Errors);
					}
				catch (Exception ex) {
					return Results.BadRequest(ex);
					}
			});
			}
		}
	}