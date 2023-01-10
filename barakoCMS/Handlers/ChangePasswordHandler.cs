using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Handlers {
	public class ChangePasswordHandler : IRequestHandler<ChangePasswordRequest, ChangePasswordResponse> {
		private readonly UserManager<IdentityUser> _userManager;

		public ChangePasswordHandler(UserManager<IdentityUser> userManager) {
			_userManager = userManager;
			}

		public async Task<ChangePasswordResponse> Handle(ChangePasswordRequest request, CancellationToken cancellationToken) {
			var user = await _userManager.FindByEmailAsync(request.Email);
			if (user == null) {
				throw new KeyNotFoundException("User not found.");
				}
			var result = await _userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
			return new ChangePasswordResponse { Result = result };
			}
		}

	}
