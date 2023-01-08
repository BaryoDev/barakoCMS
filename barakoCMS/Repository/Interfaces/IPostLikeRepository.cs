using barakoCMS.Models;

namespace barakoCMS.Repository.Interfaces {

	public interface IPostLikeRepository {

		Task<PostLike> GetByIdAsync(Guid id);
		Task<PostLike> GetByPostIdAndUserIdAsync(Guid postId, string userId);

		Task<IEnumerable<PostLike>> GetAllAsync();

		Task AddAsync(PostLike postLike);

		Task UpdateAsync(PostLike postLike);

		Task DeleteAsync(PostLike postLike);
		}
	}