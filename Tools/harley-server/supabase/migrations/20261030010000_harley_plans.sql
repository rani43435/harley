-- Plans (free / pro) per headset, and what each answer cost.
create table if not exists public.harley_devices (
  device     text primary key,
  plan       text not null default 'free',      -- 'free' | 'pro'
  pro_until  timestamptz,                        -- pro ends here unless the store purchase is checked again
  meta_user  text,                               -- Meta user id that bought pro
  created_at timestamptz not null default now(),
  last_seen  timestamptz
);
alter table public.harley_devices enable row level security;

alter table public.harley_usage add column if not exists tokens_in bigint not null default 0;
alter table public.harley_usage add column if not exists tokens_out bigint not null default 0;
alter table public.harley_usage add column if not exists cost_usd numeric(12,6) not null default 0;
alter table public.harley_usage add column if not exists provider_costs jsonb not null default '{}'::jsonb;

create or replace function public.harley_add_cost(p_device text, p_kind text, p_provider text, p_in bigint, p_out bigint, p_cost numeric)
returns void
language sql
security definer
set search_path = public
as $$
  update harley_usage
     set tokens_in = tokens_in + p_in,
         tokens_out = tokens_out + p_out,
         cost_usd = cost_usd + p_cost,
         provider_costs = jsonb_set(provider_costs, array[p_provider],
                                    to_jsonb(coalesce((provider_costs->>p_provider)::numeric, 0) + p_cost))
   where device = p_device and day = current_date and kind = p_kind;
$$;
revoke all on function public.harley_add_cost(text, text, text, bigint, bigint, numeric) from public, anon, authenticated;

-- Make your own headset pro by hand (its id is in the app's menu > Computer... or the usage table):
--   insert into harley_devices (device, plan) values ('<device id>', 'pro') on conflict (device) do update set plan = 'pro', pro_until = null;
