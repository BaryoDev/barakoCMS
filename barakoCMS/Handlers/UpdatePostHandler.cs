using barakoCMS.Repository.Interfaces;
using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Handlers {

	public class UpdatePostHandler : IRequestHandler<UpdatePostRequest, UpdatePostResponse> {
		private readonly UserManager<IdentityUser> _userManager;
		private readonly IPostRepository _postRepository;

		public UpdatePostHandler(IPostRepository postRepository, UserManager<IdentityUser> userManager) {
			_userManager = userManager;
			_postRepository = postRepository;
			}

		public async Task<UpdatePostResponse> Handle(UpdatePostRequest request, CancellationToken cancellationToken) {
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

			post.Title = request.Title;
			post.Content = request.Content;
			post.Updated = DateTime.Now;

			await _postRepository.UpdateAsync(post);
			return new UpdatePostResponse { Post = post };
			}
		}
	}