-- Undoes migrations/4.2.0/user-normalized-identity.sql, for a rollback to a release before it.
--
-- Earlier releases declare unique indexes on the stored Username and Email, and under
-- AutoCreate.CreateOnly they do not create a missing index on a table that already exists. So the
-- raw indexes come back here. They cannot fail: two raw values that were equal were also equal
-- once normalised, and the normalised index already refused that.
--
-- The NormalizedUsername and NormalizedEmail fields stay in the documents. Earlier releases ignore
-- them, and a later upgrade rewrites them.
--
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 --single-transaction -f migrations/4.2.0/rollback-user-normalized-identity.sql

DROP INDEX IF EXISTS public.mt_doc_users_uidx_normalized_username;
DROP INDEX IF EXISTS public.mt_doc_users_uidx_normalized_email;

CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_users_uidx_username
    ON public.mt_doc_users USING btree (((data ->> 'Username'::text)));
CREATE UNIQUE INDEX IF NOT EXISTS mt_doc_users_uidx_email
    ON public.mt_doc_users USING btree (((data ->> 'Email'::text)));
