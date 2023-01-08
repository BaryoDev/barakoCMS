using barakoCMS.Repository.Interfaces;
using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Handlers {

	public class DeletePostHandler : IRequestHandler<DeletePostRequest, DeletePostResponse> {
		private readonly IPostRepository _postRepository;
		private readonly UserManager<IdentityUser> _userManager;

		public DeletePostHandler(IPostRepository postRepository,
			UserManager<IdentityUser> userManager) {
			_postRepository = postRepository;
			_userManager = userManager;
			}

		public async Task<DeletePostResponse> Handle(DeletePostRequest request, CancellationToken cancellationToken) {
			var post = await _postRepository.GetByIdAsync(request.Id);
			if (post == null) {
				throw new Exception("Post not found.");
				}
			if (string.IsNullOrWhiteSpace(request.UserId)) {
				throw new Exception("User not found.");
				}
			var user = await _userManager.FindByIdAsync(request.UserId);
			if (user == null) {
				throw new Exception("User not found.");
				}
			if (post.AuthorId != user.Id) {
				throw new Exception("Unauthorized.");
				}

			await _postRepository.DeleteAsync(post);

			return new DeletePostResponse();
			}
		}
	}