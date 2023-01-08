using MediatR;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Requests {
	public class ChangePasswordRequest : IRequest<ChangePasswordResponse> {
		public string Email { get; set; }
		public string OldPassword { get; set; }
		public string NewPassword { get; set; }
		}

	public class ChangePasswordResponse {
		public IdentityResult Result { get; set; }
		}
	}
