using barakoCMS.Configurations;
using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Logging;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSwaggerConfig();

builder.Services.AddDbConfig(builder.Configuration);

builder.Services.AddRepositoryConfig();

builder.Services.AddAuthConfig(builder.Configuration);
builder.Services.AddMediatorConfig();

builder.Services.AddHandlerConfig();

builder.Services.AddCors();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
	app.UseSwagger();
	app.UseSwaggerUI();
	app.UseCors();
	app.UseAuthentication();
	app.UseRouting();
	app.UseAuthorization();
	IdentityModelEventSource.ShowPII = true;
	}

app.UseHttpsRedirection();

app.MapPost("/api/signin", async (IMediator _mediator, [FromBody] SignInRequest request) => {
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

app.MapPost("/api/signup", async (IMediator _mediator, [FromBody] SignUpRequest request) => {
	try {
		var result = await _mediator.Send(request);
		return result.Result.Succeeded ? Results.Ok() : Results.BadRequest(result.Result.Errors);
		}
	catch (Exception ex) {
		return Results.BadRequest(ex);
		}
});

app.MapPost("/api/changepassword", async (IMediator _mediator, [FromBody] ChangePasswordRequest request) => {
	try {
		var result = await _mediator.Send(request);
		return result.Result.Succeeded ? Results.Ok() : Results.BadRequest(result.Result.Errors);
		}
	catch (Exception ex) {
		return Results.BadRequest(ex);
		}
});

app.MapGet("/api/Posts", async (IMediator _mediator, [FromQuery] int pageNumber, [FromQuery] int pageSize) => {
	try {
		var result = await _mediator.Send(new GetAllPostsRequest { PageNumber = pageNumber, PageSize = pageSize });
		return Results.Ok(result);
		}
	catch (Exception ex) {
		return Results.BadRequest(ex);
		}
}).RequireAuthorization();

app.MapGet("/api/Posts/{id}", async (IMediator _mediator, Guid id) => {
	try {
		var result = await _mediator.Send(new GetPostByIdRequest { Id = id });
		return Results.Ok(result);
		}
	catch (Exception ex) {
		return Results.BadRequest(ex);
		}
}).RequireAuthorization();

app.MapPost("/api/Posts", async (IMediator _mediator, ClaimsPrincipal user, [FromBody] CreatePostRequest request) => {
	try {
		request.AuthorId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
		var result = await _mediator.Send(request);
		return Results.Created($"/api/Posts/{result.Post.Id}", result);
		}
	catch (Exception ex) {
		return Results.BadRequest(ex);
		}
}).RequireAuthorization();

app.MapPut("/api/Posts", async (IMediator _mediator, ClaimsPrincipal user, Guid id, [FromBody] UpdatePostRequest request) => {
	try {
		request.Id = id;
		request.UserId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
		var result = await _mediator.Send(request);
		return Results.Ok(result);
		}
	catch (Exception ex) {
		return Results.BadRequest(ex);
		}
}).RequireAuthorization();

app.MapDelete("/api/Posts", async (IMediator _mediator, ClaimsPrincipal user, Guid id) => {
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
}).RequireAuthorization();

app.Run();