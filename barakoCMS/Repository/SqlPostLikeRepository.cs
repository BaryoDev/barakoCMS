using barakoCMS.Models;
using barakoCMS.Repository.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace barakoCMS.Repository {

	public class SqlPostLikeRepository : IPostLikeRepository {
		private readonly AppDbContext _db;

		public SqlPostLikeRepository(AppDbContext db) {
			_db = db ?? throw new ArgumentNullException(nameof(db));
			}

		public async Task AddAsync(PostLike postLike) {
			_db.PostLikes.Add(postLike);
			await _db.SaveChangesAsync();
			}

		public async Task DeleteAsync(PostLike postLike) {
			var postLikeDb = await GetByPostIdAndUserIdAsync(postLike.PostId, postLike.UserId);
			_db.PostLikes.Remove(postLikeDb);
			await _db.SaveChangesAsync();
			}

		public Task<IEnumerable<PostLike>> GetAllAsync() {
			throw new NotImplementedException();
			}

		public Task<PostLike> GetByIdAsync(Guid id) {
			throw new NotImplementedException();
			}

		public async Task<PostLike> GetByPostIdAndUserIdAsync(Guid postId, string userId) {
			return await _db.PostLikes.Where(c => c.PostId == postId && c.UserId == userId).FirstOrDefaultAsync();
			}

		public Task UpdateAsync(PostLike postLike) {
			throw new NotImplementedException();
			}
		}
	}