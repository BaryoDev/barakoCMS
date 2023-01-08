using barakoCMS.Repository.Interfaces;
using barakoCMS.Requests;
using MediatR;

namespace barakoCMS.Handlers {

	public class GetPostByIdHandler : IRequestHandler<GetPostByIdRequest, GetPostByIdResponse> {
		private readonly IPostRepository _postRepository;

		public GetPostByIdHandler(IPostRepository postRepository) {
			_postRepository = postRepository;
			}

		public async Task<GetPostByIdResponse> Handle(GetPostByIdRequest request, CancellationToken cancellationToken) {
			var post = await _postRepository.GetByIdAsync(request.Id);
			if (post == null) {
				throw new Exception("Post not found.");
				}
			return new GetPostByIdResponse { Post = post };
			}
		}
	}