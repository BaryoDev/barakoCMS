using barakoCMS.Configurations;
using barakoCMS.Requests;
using Carter;
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

builder.Services.AddCarter();

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
	app.MapCarter();
	IdentityModelEventSource.ShowPII = true;
	}

app.UseHttpsRedirection();


app.Run();