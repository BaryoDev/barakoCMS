using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace barakoCMS.Models {
	public class AppDbContext : IdentityDbContext<IdentityUser, IdentityRole,string> {
		public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) {

			}

		public DbSet<Post> Posts { get; set; }
		public DbSet<PostLike> PostLikes { get; set; }

		protected override void OnModelCreating(ModelBuilder modelBuilder) {
			base.OnModelCreating(modelBuilder);

			modelBuilder.Entity<PostLike>()
				.HasKey(pl => new { pl.PostId, pl.UserId });
			modelBuilder.Entity<PostLike>()
				.HasOne(pl => pl.Post)
				.WithMany(p => p.Likes)
				.HasForeignKey(pl => pl.PostId);
			modelBuilder.Entity<PostLike>()
				.HasOne(pl => pl.User)
				.WithMany()
				.HasForeignKey(pl => pl.UserId);
			}
		}
	}
