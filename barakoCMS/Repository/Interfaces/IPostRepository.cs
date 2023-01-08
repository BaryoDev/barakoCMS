using barakoCMS.Models;

namespace barakoCMS.Repository.Interfaces {

	public interface IPostRepository {

		Task<Post> GetByIdAsync(Guid id);

		Task<IEnumerable<Post>> GetAllAsync(int pageNumber, int pageSize, string searchTerm);

		Task AddAsync(Post post);

		Task UpdateAsync(Post post);

		Task DeleteAsync(Post post);
		}
	}