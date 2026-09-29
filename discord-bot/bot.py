import os, time, asyncio
from datetime import datetime, timezone
import discord
from discord.ext import commands, tasks
from aiohttp import web
from dotenv import load_dotenv

load_dotenv()
TOKEN=os.environ["DISCORD_TOKEN"]
STATUS_CHANNEL_ID=int(os.environ["STATUS_CHANNEL_ID"])
UPDATES_CHANNEL_ID=int(os.environ["UPDATES_CHANNEL_ID"])
ALERTS_CHANNEL_ID=int(os.environ["ALERTS_CHANNEL_ID"])
BRIDGE_SECRET=os.environ["BRIDGE_SECRET"]
MAX_PLAYERS=int(os.getenv("MAX_PLAYERS","50"))
REGION=os.getenv("REGION","EU")
SERVER_NAME=os.getenv("SERVER_NAME","Aincrad Style")
GM_NAME=os.getenv("GM_NAME","PRECISION")
HEARTBEAT_TIMEOUT=int(os.getenv("HEARTBEAT_TIMEOUT","95"))
HTTP_HOST=os.getenv("HTTP_HOST","0.0.0.0")
HTTP_PORT=int(os.getenv("PORT", os.getenv("HTTP_PORT","8787")))

bot=commands.Bot(command_prefix="!", intents=discord.Intents.default())
tree=bot.tree
state={"online":False,"players":[],"started_at":None,"last_heartbeat":0.0,"status_message_id":None}

def now(): return datetime.now(timezone.utc)

def uptime():
    if not state["started_at"]: return "—"
    s=max(0,int((now()-state["started_at"]).total_seconds()))
    h,r=divmod(s,3600); m,_=divmod(r,60)
    return f"{h}h {m}m" if h else f"{m}m"

def mkembed(title,desc,color):
    e=discord.Embed(title=title,description=desc,color=color,timestamp=now())
    e.set_footer(text="Aincrad System • The world remembers.")
    return e

def status_embed():
    if state["online"]:
        status="🟢 ONLINE"; color=0x2ECC71; desc="The gates are open."
    else:
        status="🔴 OFFLINE"; color=0xE74C3C; desc="The world is currently unreachable."
    e=mkembed(f"✦ {SERVER_NAME.upper()} ✦",desc,color)
    e.add_field(name="Server Status",value=status,inline=True)
    e.add_field(name="Players",value=f"{len(state['players'])} / {MAX_PLAYERS}",inline=True)
    e.add_field(name="Uptime",value=uptime(),inline=True)
    e.add_field(name="Region",value=REGION,inline=True)
    if state["players"]:
        e.add_field(name="Online Now",value=", ".join(state["players"][:20]),inline=False)
    return e

async def ch(cid):
    return bot.get_channel(cid) or await bot.fetch_channel(cid)

async def update_status():
    c=await ch(STATUS_CHANNEL_ID)
    if state["status_message_id"]:
        try:
            m=await c.fetch_message(state["status_message_id"])
            await m.edit(embed=status_embed())
            return
        except Exception:
            pass
    m=await c.send(embed=status_embed())
    state["status_message_id"]=m.id
    try: await m.pin(reason="Aincrad live server status")
    except Exception: pass

async def send_update(e): await (await ch(UPDATES_CHANNEL_ID)).send(embed=e)
async def send_alert(e): await (await ch(ALERTS_CHANNEL_ID)).send(embed=e)

async def process_event(p):
    ev=p.get("event")
    state["last_heartbeat"]=time.time()

    if ev=="heartbeat":
        state["online"]=True
        if isinstance(p.get("players"),list):
            state["players"]=[str(x) for x in p["players"]]

    elif ev=="server_online":
        was=state["online"]
        state["online"]=True
        if not state["started_at"]: state["started_at"]=now()
        if not was:
            e=mkembed("🟢 SERVER ONLINE","The gates are open.\nAincrad Style is now online and ready.",0x2ECC71)
            e.add_field(name="System Note",value="Try not to pass out in the mines. Again.",inline=False)
            await send_update(e)

    elif ev=="server_stopping":
        e=mkembed("♻ SERVER RESTART","The world is restarting.\nExpected downtime: a few minutes.",0xF1C40F)
        e.add_field(name="System Note",value="Yes, your crops will survive. Probably.",inline=False)
        await send_update(e)
        state["online"]=False
        state["started_at"]=None

    elif ev=="player_join":
        name=str(p.get("player","Unknown"))
        if name not in state["players"]: state["players"].append(name)
        isgm=bool(p.get("is_gm",False)) or name.casefold()==GM_NAME.casefold()
        if isgm:
            e=mkembed("👑 GAME MASTER ONLINE",f"**{name}** has entered Aincrad.",0x3498DB)
            e.add_field(name="System",value="Administrative authority detected.\nPlease continue pretending you were behaving.",inline=False)
        else:
            e=mkembed("✦ PLAYER CONNECTED",f"**{name}** entered Aincrad.",0x5865F2)
        e.add_field(name="Players Online",value=f"{len(state['players'])} / {MAX_PLAYERS}",inline=True)
        await send_update(e)

    elif ev=="player_leave":
        name=str(p.get("player","Unknown"))
        state["players"]=[x for x in state["players"] if x!=name]
        e=mkembed("✦ PLAYER DISCONNECTED",f"**{name}** left the world.",0x95A5A6)
        e.add_field(name="System Note",value="They’ll be back. They always come back.",inline=False)
        await send_update(e)

    elif ev=="save_complete":
        e=mkembed("💾 WORLD SAVED","The current world state has been secured.",0x9B59B6)
        e.add_field(name="System Note",value="Your questionable financial decisions are now permanent.",inline=False)
        await send_update(e)

    await update_status()

async def event_endpoint(request):
    if request.headers.get("X-Aincrad-Secret")!=BRIDGE_SECRET:
        return web.json_response({"ok":False},status=401)
    try:
        p=await request.json()
    except Exception:
        return web.json_response({"ok":False},status=400)
    await process_event(p)
    return web.json_response({"ok":True})

async def health_endpoint(request):
    return web.json_response({"ok":True,"service":"Aincrad System"})

@tasks.loop(seconds=20)
async def watchdog():
    if state["online"] and state["last_heartbeat"] and time.time()-state["last_heartbeat"]>HEARTBEAT_TIMEOUT:
        state["online"]=False
        state["started_at"]=None
        e=mkembed("⚠ CONNECTION LOST","Aincrad stopped responding unexpectedly.",0xE74C3C)
        e.add_field(name="System",value="Automatic monitoring is still active.\nThe chickens deny involvement.",inline=False)
        await send_alert(e)
        await update_status()

@tree.command(name="status",description="Show Aincrad server status.")
async def status_cmd(i:discord.Interaction):
    await i.response.send_message(embed=status_embed())

@tree.command(name="players",description="Show players currently online.")
async def players_cmd(i:discord.Interaction):
    text="\n".join(f"• {x}" for x in state["players"]) or "No players are currently online."
    await i.response.send_message(embed=mkembed("👥 ONLINE PLAYERS",text,0x5865F2))

@tree.command(name="uptime",description="Show Aincrad server uptime.")
async def uptime_cmd(i:discord.Interaction):
    await i.response.send_message(embed=mkembed("⏱ SERVER UPTIME",uptime(),0x5865F2))

@tree.command(name="serverinfo",description="Show Aincrad server information.")
async def serverinfo_cmd(i:discord.Interaction):
    e=mkembed("✦ AINCRAD STYLE","A persistent modded Stardew Valley world.",0x3498DB)
    e.add_field(name="Region",value=REGION,inline=True)
    e.add_field(name="Capacity",value=f"{MAX_PLAYERS} players",inline=True)
    await i.response.send_message(embed=e)

@bot.event
async def on_ready():
    await tree.sync()
    if not watchdog.is_running(): watchdog.start()
    await update_status()
    print(f"Logged in as {bot.user}")

async def main():
    app=web.Application()
    app.router.add_post("/event",event_endpoint)
    app.router.add_get("/health",health_endpoint)
    runner=web.AppRunner(app)
    await runner.setup()
    await web.TCPSite(runner,HTTP_HOST,HTTP_PORT).start()
    async with bot:
        await bot.start(TOKEN)

asyncio.run(main())
