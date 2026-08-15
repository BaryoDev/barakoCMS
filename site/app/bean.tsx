/** The project's logo, inline so it needs no network request and inherits the page's colours. */
export function Bean({ size = 40, className = '' }: { size?: number; className?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 128 128"
      role="img"
      aria-label="BarakoCMS"
      className={className}
    >
      <defs>
        <linearGradient id="beanFill" x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" stopColor="#A9744F" />
          <stop offset="0.55" stopColor="#6F4E37" />
          <stop offset="1" stopColor="#3E2418" />
        </linearGradient>
      </defs>
      <g transform="rotate(-32 64 64)">
        <ellipse cx="64" cy="64" rx="33" ry="50" fill="url(#beanFill)" />
        <ellipse cx="52" cy="44" rx="9" ry="17" fill="#C89B76" opacity="0.35" />
        <path
          d="M64 17 C 51 41, 77 55, 64 64 C 51 73, 77 87, 64 111"
          fill="none"
          stroke="#2A170D"
          strokeWidth="6.5"
          strokeLinecap="round"
        />
      </g>
    </svg>
  );
}
