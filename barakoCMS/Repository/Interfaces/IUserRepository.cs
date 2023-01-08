using Microsoft.AspNetCore.Identity;

namespace barakoCMS.Repository.Interfaces {

	public interface IUserRepository {

		Task<IdentityUser> GetById(string userId);
		}
	}