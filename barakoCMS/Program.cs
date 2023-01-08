using barakoCMS.Handlers;
using barakoCMS.Models;
using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

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

builder.Services.AddAuthentication(options => {
	options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
	options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options => {
	options.Authority = "https://your-authority.com";
	options.Audience = "your-api-identifier";
});

builder.Services.AddMediatR(typeof(SignUpHandler), typeof(SignInHandler), typeof(ChangePasswordHandler));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment()) {
	app.UseSwagger();
	app.UseSwaggerUI();
	app.UseAuthentication();
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

app.Run();
