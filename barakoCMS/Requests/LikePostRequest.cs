using MediatR;

namespace barakoCMS.Requests {
	public class LikePostRequest : IRequest<LikePostResponse> {
		public Guid PostId { get; set; }
		public string UserId { get; set; }
		}
	public class LikePostResponse {
		public bool Success { get; set; }
		}


	}
