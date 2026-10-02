# The data checks upgrade-check.sh makes around migrations/4.6.0/sensitivity-by-capability.sql and
# its rollback (#883). Sourced, not run.
#
# They are here and not inline so scripts/test-check-ports.sh can call each one with a query
# function of its own. The rollback check sits after the whole upgrade in upgrade-check.sh, where
# no stubbed run reaches, and a check nothing can make fail is not known to work.
#
# The caller provides:
#   psql_q <sql>     prints the one value the query returns
#   fail <message>   reports and exits
#   FROM_VERSION     the release the database was created by, for the messages
#
# The seeded HR role is the one stored under the seeded id and still named exactly HR. That is the
# role the forward file and the seeder both grant view_sensitive to, and the only one.

SEEDED_HR="id = '00000000-0000-0000-0000-000000000003' and data ->> 'Name' = 'HR'"

seeded_hr_count() {
    psql_q "select count(*) from mt_doc_roles where $SEEDED_HR;"
}

seeded_hr_holding_view_sensitive_count() {
    psql_q "select count(*) from mt_doc_roles where $SEEDED_HR and data -> 'SystemCapabilities' ? 'view_sensitive';"
}

# Before the forward file. Without the role the two checks after it would describe nothing.
require_seeded_hr() {
    [ "$(seeded_hr_count)" = "1" ] \
        || fail "the ${FROM_VERSION} database has no role named HR under the seeded id, so migrations/4.6.0/sensitivity-by-capability.sql proves nothing on this start"
}

# After the forward file, and before the new build boots: its seeder grants the same capability and
# would hide a file that does nothing.
require_seeded_hr_granted() {
    [ "$(seeded_hr_holding_view_sensitive_count)" = "1" ] \
        || fail "migrations/4.6.0/sensitivity-by-capability.sql did not give the HR role view_sensitive, so its holders would stop reading Sensitive fields on upgrade"
}

# After the rollback file.
require_seeded_hr_not_granted() {
    [ "$(seeded_hr_holding_view_sensitive_count)" = "0" ] \
        || fail "migrations/4.6.0/rollback-sensitivity-by-capability.sql left view_sensitive on the seeded HR role, which the forward file put there"
}
