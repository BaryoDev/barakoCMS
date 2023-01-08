using barakoCMS.Models;
using barakoCMS.Repository.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Repository {

	public class UserRepository : IUserRepository {
		private readonly AppDbContext _db;

		public UserRepository(AppDbContext db) {
			_db = db ?? throw new ArgumentNullException(nameof(db));
			}

		public async Task<IdentityUser> GetById(string userId) {
			return await _db.Users.FindAsync(userId);
			}
		}
	}