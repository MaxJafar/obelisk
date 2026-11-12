"""Headless synthetic-tour checks. Never connects to a real device or opens a microphone."""
import argparse
import functools
import http.server
from pathlib import Path
import threading
from playwright.sync_api import sync_playwright, expect

ROOT=Path(__file__).resolve().parents[1]
class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self,*args): pass

parser=argparse.ArgumentParser();parser.add_argument('--edge',action='store_true');parser.add_argument('--capture',action='store_true');args=parser.parse_args()
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(QuietHandler,directory=str(ROOT/'docs')))
threading.Thread(target=server.serve_forever,daemon=True).start()
url=f'http://127.0.0.1:{server.server_port}'
try:
 with sync_playwright() as p:
  browser=p.chromium.launch(headless=True,**({'channel':'msedge'} if args.edge else {}))
  page=browser.new_page(viewport={'width':1440,'height':920},device_scale_factor=1)
  errors=[];page.on('pageerror',lambda e:errors.append(str(e)))
  page.goto(url+'/tour/')
  expect(page.locator('#node-count')).to_have_text('3 / 3')
  expect(page.locator('#mic')).to_be_disabled()
  expect(page.locator('#composer button')).to_be_disabled()
  initial=page.locator('.node').count()
  page.get_by_role('button',name='Storage',exact=True).click()
  assert page.locator('.node').count()>initial,'Storage layer did not add volumes'
  page.get_by_role('button',name='Storage',exact=True).click()
  page.locator('.node[aria-label^="Workstation "]').click()
  expect(page.locator('#detail-title')).to_have_text('Workstation')
  page.get_by_role('button',name='Explore device').click()
  expect(page.locator('#back')).to_be_visible()
  assert page.locator('.node').count()<initial,'Host focus did not narrow the map'
  page.locator('#back').click()
  page.locator('#search').fill('Jarvis')
  expect(page.locator('.node.dim').first).to_be_visible()
  page.locator('#search').fill('')
  page.locator('#tour-motion').click()
  expect(page.locator('#phase')).to_have_text('LISTENING')
  page.locator('#stop').click()
  expect(page.locator('#phase')).to_have_text('STANDBY')
  page.locator('#settings').click()
  expect(page.locator('#settings-dialog')).to_be_visible()
  expect(page.locator('#setup-save')).to_be_disabled()
  page.locator('#settings-close').click()
  page.locator('#fit').click()
  if args.capture: page.screenshot(path=str(ROOT/'docs/assets/obelisk-dashboard.png'))
  for width,height in [(1040,720),(390,844)]:
   page.set_viewport_size({'width':width,'height':height})
   page.wait_for_timeout(150)
   assert page.evaluate('document.documentElement.scrollWidth <= innerWidth'),f'Horizontal overflow at {width}'
   assert page.locator('#map-stage').bounding_box()['height']>=250
  page.goto(url+'/')
  expect(page.get_by_role('heading',name='Your systems. In sync.')).to_be_visible()
  page.get_by_text('What runs locally?',exact=True).click()
  expect(page.locator('details').first).to_have_attribute('open','')
  assert not errors,errors
  browser.close()
 print('PASS tour layers, node inspector, host focus, search, voice motion, settings, responsive layout and landing page')
finally: server.shutdown();server.server_close()
