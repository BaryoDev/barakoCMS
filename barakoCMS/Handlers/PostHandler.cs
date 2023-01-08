using barakoCMS.Models;
using barakoCMS.Requests;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace barakoCMS.Handlers {

	public class GetAllPostsHandler : IRequestHandler<GetAllPostsRequest, GetAllPostsResponse> {
		private readonly AppDbContext _db;

		public GetAllPostsHandler(AppDbContext db) {
			_db = db;
			}

		public async Task<GetAllPostsResponse> Handle(GetAllPostsRequest request, CancellationToken cancellationToken) {
			var posts = await _db.Posts
			.Include(p => p.Likes)
			.Include(p => p.Author)
			.Where(p => p.Content.Contains(request.SearchTerm) || p.Author.UserName.Contains(request.SearchTerm))
			.OrderByDescending(p => p.Created)
			.Skip((request.PageNumber - 1) * request.PageSize)
			.Take(request.PageSize)
			.ToListAsync();
			return new GetAllPostsResponse { Posts = posts };
			}
		}

	public class GetPostByIdHandler : IRequestHandler<GetPostByIdRequest, GetPostByIdResponse> {
		private readonly AppDbContext _db;

		public GetPostByIdHandler(AppDbContext db) {
			_db = db;
			}

		public async Task<GetPostByIdResponse> Handle(GetPostByIdRequest request, CancellationToken cancellationToken) {
			var post = await _db.Posts.FindAsync(request.Id);
			if (post == null) {
				throw new Exception("Post not found.");
				}
			return new GetPostByIdResponse { Post = post };
			}
		}

	public class CreatePostHandler : IRequestHandler<CreatePostRequest, CreatePostResponse> {
		private readonly AppDbContext _db;
		private readonly UserManager<IdentityUser> _userManager;

		public CreatePostHandler(AppDbContext db, UserManager<IdentityUser> userManager) {
			_db = db;
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
			await _db.Posts.AddAsync(post);
			await _db.SaveChangesAsync();
			return new CreatePostResponse { Post = post };
			}
		}

	public class UpdatePostHandler : IRequestHandler<UpdatePostRequest, UpdatePostResponse> {
		private readonly AppDbContext _db;
		private readonly UserManager<IdentityUser> _userManager;

		public UpdatePostHandler(AppDbContext db,
			UserManager<IdentityUser> userManager) {
			_db = db;
			_userManager = userManager;
			}

		public async Task<UpdatePostResponse> Handle(UpdatePostRequest request, CancellationToken cancellationToken) {
			var post = await _db.Posts.FindAsync(request.Id);
			if (post == null) {
				throw new Exception("Post not found.");
				}
			if (string.IsNullOrWhiteSpace(request.UserId)) {
				throw new Exception("User not found.");
				}
			var user = await _userManager.FindByIdAsync(request.UserId);
			if (user == null) {
				throw new Exception("User not found.");
				}
			if (post.AuthorId != user.Id) {
				throw new Exception("Unauthorized.");
				}
			post.Title = request.Title;
			post.Content = request.Content;
			post.Updated = DateTime.Now;
			await _db.SaveChangesAsync();
			return new UpdatePostResponse { Post = post };
			}
		}
	}

public class DeletePostHandler : IRequestHandler<DeletePostRequest, DeletePostResponse> {
	private readonly AppDbContext _db;
	private readonly UserManager<IdentityUser> _userManager;

	public DeletePostHandler(AppDbContext db,
		UserManager<IdentityUser> userManager) {
		_db = db;
		_userManager = userManager;
		}

	public async Task<DeletePostResponse> Handle(DeletePostRequest request, CancellationToken cancellationToken) {
		var post = await _db.Posts.FindAsync(request.Id);
		if (post == null) {
			throw new Exception("Post not found.");
			}
		if (string.IsNullOrWhiteSpace(request.UserId)) {
			throw new Exception("User not found.");
			}
		var user = await _userManager.FindByIdAsync(request.UserId);
		if (user == null) {
			throw new Exception("User not found.");
			}
		if (post.AuthorId != user.Id) {
			throw new Exception("Unauthorized.");
			}
		_db.Posts.Remove(post);
		await _db.SaveChangesAsync();
		return new DeletePostResponse();
		}

	public class LikePostHandler : IRequestHandler<LikePostRequest, LikePostResponse> {
		private readonly AppDbContext _db;

		public LikePostHandler(AppDbContext db) {
			_db = db;
			}

		public async Task<LikePostResponse> Handle(LikePostRequest request, CancellationToken cancellationToken) {
			var post = await _db.Posts.FindAsync(request.PostId);
			if (post == null) {
				throw new Exception("Post not found.");
				}
			var user = await _db.Users.FindAsync(request.UserId);
			if (user == null) {
				throw new Exception("User not found.");
				}
			var like = await _db.PostLikes.FindAsync(request.PostId, request.UserId);
			if (like == null) {
				like = new PostLike {
					PostId = request.PostId,
					UserId = request.UserId
					};
				_db.PostLikes.Add(like);
				}
			else {
				_db.PostLikes.Remove(like);
				}
			await _db.SaveChangesAsync();
			return new LikePostResponse { Success = true };
			}
		}
	}