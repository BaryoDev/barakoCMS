using barakoCMS.Models;
using barakoCMS.Repository.Interfaces;
using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Handlers {

	public class CreatePostHandler : IRequestHandler<CreatePostRequest, CreatePostResponse> {
		private readonly IPostRepository _postRepository;
		private readonly UserManager<IdentityUser> _userManager;

		public CreatePostHandler(IPostRepository postRepository, UserManager<IdentityUser> userManager) {
			_postRepository = postRepository;
			_userManager = userManager;
			}

		public async Task<CreatePostResponse> Handle(CreatePostRequest request, CancellationToken cancellationToken) {
			var author = await _userManager.FindByIdAsync(request.AuthorId);
			if (author == null) {
				throw new Exception("Author not found.");
				}

			var post = new Post {
				Id = Guid.NewGuid(),
				Title = request.Title,
				Content = request.Content,
				Author = author,
				Created = DateTime.Now,
				Updated = DateTime.Now
				};
			await _postRepository.AddAsync(post);
			return new CreatePostResponse { Post = post };
			}
		}
	}