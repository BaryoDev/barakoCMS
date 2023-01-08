using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace barakoCMS.Handlers {

	public class SignInHandler : IRequestHandler<SignInRequest, SignInResponse> {
		private readonly SignInManager<IdentityUser> _signInManager;
		private readonly UserManager<IdentityUser> _userManager;
		private readonly IConfiguration _configuration;

		public SignInHandler(SignInManager<IdentityUser> signInManager, UserManager<IdentityUser> userManager, IConfiguration configuration) {
			_signInManager = signInManager;
			_userManager = userManager;
			_configuration = configuration;
			}

		public async Task<SignInResponse> Handle(SignInRequest request, CancellationToken cancellationToken) {
			var result = await _signInManager.PasswordSignInAsync(request.Email, request.Password, false, false);
			if (result.Succeeded) {
				var user = await _userManager.FindByEmailAsync(request.Email);
				var claims = new[]
				{
				new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
				new Claim(ClaimTypes.Email, user.Email) ,
			};
				var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["JWT:Key"]));
				var token = new JwtSecurityToken(
					issuer: _configuration["JWT:Issuer"],
					audience: _configuration["JWT:Audience"],
					claims: claims,
					expires: DateTime.UtcNow.AddDays(30),
					signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
				);
				return new SignInResponse { Token = new JwtSecurityTokenHandler().WriteToken(token) };
				}
			else {
				return new SignInResponse { Message = "Invalid Sign In" };
				}
			}
		}
	}