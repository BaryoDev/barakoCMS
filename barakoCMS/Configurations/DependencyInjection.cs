using barakoCMS.Handlers;
using barakoCMS.Models;
using barakoCMS.Repository;
using barakoCMS.Repository.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace barakoCMS.Configurations {

	public static class DependencyInjection {

		public static IServiceCollection AddSwaggerConfig(this IServiceCollection services) {
			services.AddEndpointsApiExplorer();
			services.AddSwaggerGen();
			return services;
			}

		public static IServiceCollection AddDbConfig(this IServiceCollection services, IConfiguration configuration) {
			services.AddDbContext<AppDbContext>(options =>
				options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));
			services.AddIdentity<IdentityUser, IdentityRole>()
				.AddEntityFrameworkStores<AppDbContext>()
				.AddDefaultTokenProviders();
			return services;
			}

		public static IServiceCollection AddAuthConfig(this IServiceCollection services, IConfiguration configuration) {
			string issuer = configuration.GetValue<string>("Jwt:Issuer");
			string signingKey = configuration.GetValue<string>("Jwt:Key");
			byte[] signingKeyBytes = System.Text.Encoding.UTF8.GetBytes(signingKey);

			services.AddAuthentication(options => {
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
			services.AddAuthorization();
			return services;
			}

		public static IServiceCollection AddRepositoryConfig(this IServiceCollection services) {
			services.AddScoped<IPostRepository, SqlPostRepository>();
			services.AddScoped<IUserRepository, UserRepository>();
			return services;
			}

		public static IServiceCollection AddHandlerConfig(this IServiceCollection services) {
			services.AddScoped<SignInHandler>();
			services.AddScoped<SignUpHandler>();
			services.AddScoped<ChangePasswordHandler>();
			services.AddScoped<GetAllPostsHandler>();
			services.AddScoped<GetPostByIdHandler>();
			services.AddScoped<CreatePostHandler>();
			services.AddScoped<UpdatePostHandler>();
			services.AddScoped<DeletePostHandler>();
			return services;
			}
		}
	}