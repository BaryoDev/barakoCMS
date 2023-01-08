using barakoCMS.Handlers;
using barakoCMS.Models;
using barakoCMS.Repository;
using barakoCMS.Repository.Interfaces;
using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Logging;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

//Database
builder.Services.AddDbContext<AppDbContext>(options =>
	options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddIdentity<IdentityUser, IdentityRole>()
	.AddEntityFrameworkStores<AppDbContext>()
	.AddDefaultTokenProviders(); ;

builder.Services.AddScoped<IPostRepository, SqlPostRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();

string issuer = builder.Configuration.GetValue<string>("Jwt:Issuer");
string signingKey = builder.Configuration.GetValue<string>("Jwt:Key");
byte[] signingKeyBytes = System.Text.Encoding.UTF8.GetBytes(signingKey);

builder.Services.AddAuthentication(options => {
	options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
	options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options => {
	options.RequireHttpsMetadata = false;
	options.SaveToken = true;
	options.TokenValidationParameters = new TokenValidationParameters() {
		ValidateIssuer = true,
		ValidIssuer = issuer,
		ValidateAudience = true,
		ValidAudience = issuer,
		ValidateLifetime = true,
		ValidateIssuerSigningKey = true,
		ClockSkew = System.TimeSpan.Zero,
		IssuerSigningKey = new SymmetricSecurityKey(signingKeyBytes)
		};
});
builder.Services.AddAuthorization();

builder.Services.AddMediatR(typeof(SignUpHandler), typeof(SignInHandler),
	typeof(ChangePasswordHandler), typeof(GetAllPostsHandler), typeof(GetPostByIdHandler),
	typeof(CreatePostHandler), typeof(UpdatePostHandler), typeof(DeletePostHandler));

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
});

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