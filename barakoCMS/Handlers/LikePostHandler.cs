using barakoCMS.Models;
using barakoCMS.Repository.Interfaces;
using barakoCMS.Requests;
using MediatR;

namespace barakoCMS.Handlers {

	public class LikePostHandler : IRequestHandler<LikePostRequest, LikePostResponse> {
		private readonly IPostRepository _postRepository;
		private readonly IPostLikeRepository _postLikeRepository;
		private readonly IUserRepository _userRepository;

		public LikePostHandler(
			IPostRepository postRepository,
			IPostLikeRepository postLikeRepository,
			IUserRepository userRepository) {
			_postRepository = postRepository;
			_postLikeRepository = postLikeRepository;
			_userRepository = userRepository;
			}

		public async Task<LikePostResponse> Handle(LikePostRequest request, CancellationToken cancellationToken) {
			var post = await _postRepository.GetByIdAsync(request.PostId);
			if (post == null) {
				throw new Exception("Post not found.");
				}
			var user = await _userRepository.GetById(request.UserId);
			if (user == null) {
				throw new Exception("User not found.");
				}
			var like = await _postLikeRepository.GetByPostIdAndUserIdAsync(request.PostId, request.UserId);
			if (like == null) {
				like = new PostLike {
					PostId = request.PostId,
					UserId = request.UserId
					};
				await _postLikeRepository.AddAsync(like);
				}
			else {
				await _postLikeRepository.DeleteAsync(like);
				}
			return new LikePostResponse { Success = true };
			}
		}
	}