using barakoCMS.Repository.Interfaces;
using barakoCMS.Requests;
using MediatR;

namespace barakoCMS.Handlers {
	public class GetAllPostsHandler : IRequestHandler<GetAllPostsRequest, GetAllPostsResponse> {
		private readonly IPostRepository _postRepository;

		public GetAllPostsHandler(IPostRepository postRepository) {
			_postRepository = postRepository;
			}

		public async Task<GetAllPostsResponse> Handle(GetAllPostsRequest request, CancellationToken cancellationToken) {
			var posts = await _postRepository.GetAllAsync(request.PageNumber, request.PageSize, request.SearchTerm);
			return new GetAllPostsResponse { Posts = posts.ToList() };
			}
		}
	}
