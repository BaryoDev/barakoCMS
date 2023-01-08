using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Handlers {
	public class SignUpHandler : IRequestHandler<SignUpRequest, SignUpResponse> {
		private readonly UserManager<IdentityUser> _userManager;

		public SignUpHandler(UserManager<IdentityUser> userManager) {
			_userManager = userManager;
			}

		public async Task<SignUpResponse> Handle(SignUpRequest request, CancellationToken cancellationToken) {
			var user = new IdentityUser { UserName = request.Email, Email = request.Email };
			var result = await _userManager.CreateAsync(user, request.Password);
			return new SignUpResponse { Result = result };
			}
		}
	}
