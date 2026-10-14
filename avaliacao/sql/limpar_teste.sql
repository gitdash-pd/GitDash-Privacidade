-- Remove os registos fictícios criados por seed_retencao.sql.
delete from repository where name like 'pd-teste/%';
delete from log where visited_endpoint like '/pd-teste/%';
