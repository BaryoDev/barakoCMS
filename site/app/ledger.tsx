/*
 * The hero's argument, made in the product's own vocabulary.
 *
 * Every headless CMS can show a content form. This one is event sourced on Marten, so a record has
 * a past rather than one row that keeps being overwritten, and that is the thing worth putting
 * first. The stream is real: these are the event names the core actually appends, in the order a
 * scheduled article goes through.
 *
 * The sequence numbers are load-bearing rather than ornamental. An event stream is ordered, and
 * the order is what makes the record reconstructible, so numbering it says something true.
 */

const EVENTS: Array<{ seq: string; name: string; detail: string }> = [
  { seq: '1', name: 'ContentCreated', detail: 'type: article, status: Draft' },
  { seq: '2', name: 'FieldUpdated', detail: 'title, body, author' },
  { seq: '3', name: 'ContentScheduled', detail: 'publishAt: 2026-03-04T09:00Z' },
  { seq: '4', name: 'ContentStatusChanged', detail: 'Draft to Published' },
];

export function Ledger() {
  return (
    <div className="font-mono text-[12.5px] leading-relaxed">
      <div className="flex items-center gap-2.5 text-on-roast-3">
        <span className="eyebrow">Event stream</span>
        <span className="h-px flex-1 bg-roast-rule" />
        <span className="text-[11px]">append only</span>
      </div>

      <ol className="mt-4 space-y-px">
        {EVENTS.map((e) => (
          <li
            key={e.seq}
            className="ledger-row flex items-baseline gap-3 rounded-sm bg-roast-2 px-3 py-2.5"
          >
            <span className="text-on-roast-3 tabular-nums">{e.seq}</span>
            <span className="text-bean-soft">{e.name}</span>
            <span className="ml-auto hidden truncate text-on-roast-3 sm:block">{e.detail}</span>
          </li>
        ))}
      </ol>

      <div className="ledger-body mt-5">
        {/*
          Deliberately not the .eyebrow class. That uppercases, and an uppercased URL path is not a
          URL any more; a developer reads it as invented. Paths keep their own casing.
        */}
        <div className="flex items-center gap-2.5 text-on-roast-3">
          <span className="text-[11px] tracking-[.04em]">
            <span className="text-on-roast-2">GET</span> /api/delivery/article/spring-roast
          </span>
          <span className="h-px flex-1 bg-roast-rule" />
        </div>
        <pre className="mt-4 overflow-x-auto rounded-sm border border-roast-rule bg-roast-2 p-4 text-on-roast-2">
          <code>{`{
  "title": "Spring roast notes",
  "status": "Published",
  "publishedAt": "2026-03-04T09:00:00Z",
  "author": { "name": "Rosa" }
}`}</code>
        </pre>
        {/*
          The point of the whole panel. Without this line a reader sees a log and a response and has
          to work out for themselves that one produced the other.
        */}
        <p className="mt-4 text-[12px] text-on-roast-3">
          Four events in, one record out. The stream stays, so you can ask what it looked like
          before.
        </p>
      </div>
    </div>
  );
}
