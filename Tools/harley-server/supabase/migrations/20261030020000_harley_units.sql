-- Usage measured in "units" (what each request really used: tokens, with answers weighted more),
-- so the daily limit is a percentage like Claude's usage meter instead of a count of questions.
alter table public.harley_usage add column if not exists units bigint not null default 0;

create or replace function public.harley_add_usage(p_device text, p_kind text, p_provider text,
                                                   p_in bigint, p_out bigint, p_cost numeric, p_units bigint)
returns void
language sql
security definer
set search_path = public
as $$
  update harley_usage
     set tokens_in = tokens_in + p_in,
         tokens_out = tokens_out + p_out,
         cost_usd = cost_usd + p_cost,
         units = units + p_units,
         provider_costs = case when p_provider = '' then provider_costs else
           jsonb_set(provider_costs, array[p_provider],
                     to_jsonb(coalesce((provider_costs->>p_provider)::numeric, 0) + p_cost)) end
   where device = p_device and day = current_date and kind = p_kind;
$$;
revoke all on function public.harley_add_usage(text, text, text, bigint, bigint, numeric, bigint) from public, anon, authenticated;

create or replace function public.harley_units_today(p_device text)
returns bigint
language sql
stable
security definer
set search_path = public
as $$
  select coalesce(sum(units), 0)::bigint from harley_usage where device = p_device and day = current_date;
$$;
revoke all on function public.harley_units_today(text) from public, anon, authenticated;
