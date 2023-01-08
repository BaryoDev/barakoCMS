using MediatR;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Requests {
	public class SignUpRequest : IRequest<SignUpResponse> {
		public string Email { get; set; }
		public string Password { get; set; }
		}

	public class SignUpResponse {
		public IdentityResult Result { get; set; }
		}
	}
