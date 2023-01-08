using MediatR;

namespace barakoCMS.Requests {
	public class SignInRequest : IRequest<SignInResponse> {
		public string Email { get; set; }
		public string Password { get; set; }
		}

	public class SignInResponse {
		public string Token { get; set; }
		public string Message { get; set; }
		}
	}
