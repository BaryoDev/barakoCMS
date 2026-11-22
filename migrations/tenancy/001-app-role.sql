-- Hand the barakoCMS tables to a role that row level security can bind.
--
-- This file is kept at its old path so the command in docs/tenancy-at-the-database.md still works.
-- The script itself is scripts/db-app-role.sql, which is meant to run on every start. Read that file
-- for what it does and why.
--
--   psql -d barako -v app_role=barako_app -v app_password='...' -f migrations/tenancy/001-app-role.sql
--
-- \ir reads the path relative to this file, so the command works from any directory. It is not a
-- migration: db-migrate never lists this folder.

\ir ../../scripts/db-app-role.sql
