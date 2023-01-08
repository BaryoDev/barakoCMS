using barakoCMS.Models;
using barakoCMS.Repository.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace barakoCMS.Repository {

	public class SqlPostRepository : IPostRepository {
		private readonly AppDbContext _db;

		public SqlPostRepository(AppDbContext db) {
			_db = db ?? throw new ArgumentNullException(nameof(db));
			}

		public async Task AddAsync(Post post) {
			await _db.Posts.AddAsync(post);
			await _db.SaveChangesAsync();
			}

		public async Task DeleteAsync(Post post) {
			var postDb = await _db.Posts.FindAsync(post.Id);
			if (postDb != null) {
				_db.Posts.Remove(postDb);
				await _db.SaveChangesAsync();
				}
			}

		public async Task<IEnumerable<Post>> GetAllAsync(int pageNumber, int pageSize, string searchTerm) {
			return await _db.Posts
			.Include(p => p.Likes)
			.Include(p => p.Author)
			.Where(p => p.Content.Contains(searchTerm) || p.Author.UserName.Contains(searchTerm))
			.OrderByDescending(p => p.Created)
			.Skip((pageNumber - 1) * pageSize)
			.Take(pageSize)
			.ToListAsync();
			}

		public async Task<Post> GetByIdAsync(Guid id) {
			return await _db.Posts.FindAsync(id);
			}

		public async Task UpdateAsync(Post post) {
			var postDb = await _db.Posts.FindAsync(post.Id);
			if (postDb != null) {
				postDb.Title = post.Title;
				postDb.Content = post.Content;
				postDb.Updated = DateTime.Now;

				await _db.SaveChangesAsync();
				}
			}
		}
	}