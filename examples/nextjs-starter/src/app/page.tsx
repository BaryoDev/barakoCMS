import { fetchContent, Post, BarakoContent } from '@/lib/barako';
import { Newspaper, ArrowRight, LayoutDashboard } from 'lucide-react';
import Link from 'next/link';

export default async function Home() {
    let posts: BarakoContent[] = [];
    let error = null;

    try {
        posts = await fetchContent('post');
    } catch (e) {
        console.error("Failed to fetch posts", e);
        error = "Could not connect to BarakoCMS. Is it running on port 5005?";
    }

    return (
        <main className="min-h-screen bg-neutral-50 p-8 font-[family-name:var(--font-geist-sans)]">
            <header className="max-w-4xl mx-auto mb-12 flex justify-between items-center">
                <div>
                    <h1 className="text-3xl font-bold bg-gradient-to-r from-orange-600 to-red-600 bg-clip-text text-transparent">
                        BarakoCMS Starter
                    </h1>
                    <p className="text-neutral-500">Next.js 15 App Router + Tailwind</p>
                </div>
                <a
                    href="http://localhost:5005/health-ui"
                    target="_blank"
                    className="flex items-center gap-2 px-4 py-2 bg-white border border-neutral-200 rounded-lg hover:bg-neutral-50 transition"
                >
                    <LayoutDashboard size={16} />
                    <span>CMS Status</span>
                </a>
            </header>

            <section className="max-w-4xl mx-auto">
                {error ? (
                    <div className="p-6 bg-red-50 border border-red-200 rounded-xl text-red-700">
                        <h3 className="font-bold flex items-center gap-2">
                            Connection Error
                        </h3>
                        <p className="mt-1">{error}</p>
                        <p className="text-sm mt-4 opacity-75">Check .env.local matches your Docker port.</p>
                    </div>
                ) : posts.length === 0 ? (
                    <div className="text-center py-20 bg-white rounded-2xl border border-neutral-200 shadow-sm">
                        <div className="bg-orange-100 w-16 h-16 rounded-full flex items-center justify-center mx-auto mb-6">
                            <Newspaper className="text-orange-600" size={32} />
                        </div>
                        <h2 className="text-2xl font-bold text-neutral-900 mb-2">No Content Found</h2>
                        <p className="text-neutral-500 max-w-md mx-auto mb-8">
                            Your CMS allows you to define content types dynamically.
                            Create a content type named <code>post</code> to see it appear here.
                        </p>
                        <a
                            href="http://localhost:5005/admin"
                            className="inline-flex items-center gap-2 bg-neutral-900 text-white px-6 py-3 rounded-full hover:bg-neutral-800 transition"
                        >
                            Go to CMS Admin <ArrowRight size={16} />
                        </a>
                    </div>
                ) : (
                    <div className="grid md:grid-cols-2 gap-6">
                        {posts.map((post) => (
                            <article key={post.id} className="bg-white p-6 rounded-xl border border-neutral-200 hover:shadow-md transition group">
                                <div className="mb-4">
                                    <span className="text-xs font-medium bg-orange-100 text-orange-800 px-2 py-1 rounded">
                                        Post
                                    </span>
                                </div>
                                <h3 className="text-xl font-bold mb-2 group-hover:text-orange-600 transition">
                                    {post.data?.title || "Untitled Post"}
                                </h3>
                                <p className="text-neutral-500 line-clamp-2 mb-4">
                                    {post.data?.excerpt || "No excerpt provided."}
                                </p>
                                <div className="text-sm text-neutral-400">
                                    {new Date(post.createdAt).toLocaleDateString()}
                                </div>
                            </article>
                        ))}
                    </div>
                )}
            </section>
        </main>
    );
}
