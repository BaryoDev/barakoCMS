using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace barakoCMS.Models {
	public class AppDbContext : IdentityDbContext<IdentityUser, IdentityRole,string> {
		public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) {

			}
		}
	}
