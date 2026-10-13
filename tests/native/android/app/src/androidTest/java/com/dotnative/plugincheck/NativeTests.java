package com.dotnative.plugincheck;
import android.test.ActivityInstrumentationTestCase2;
import android.view.View;
import android.view.ViewGroup;
import android.widget.TextView;
import com.dotnative.plugins.*;
import java.util.*;
import java.util.concurrent.TimeUnit;
public class NativeTests extends ActivityInstrumentationTestCase2<HarnessActivity> {
 public NativeTests(){super(HarnessActivity.class);}
 private PluginReply call(String channel,String method,Map<String,Object> args) {
  PluginReply reply=new PluginReply();
  getInstrumentation().runOnMainSync(()->NativeChannels.INSTANCE.channel(channel).call(method,args,reply));
  return reply;
 }
 public void testAdsConsentGateWithoutNetworkRequests() throws Exception {
  getActivity();Map<String,Object> args=new HashMap<>();args.put("id","test");args.put("adUnitId","ca-app-pub-3940256099942544/6300978111");
  PluginReply blocked=call("dotnative.ads.placements","loadBanner",args);
  assertEquals("consent_required",blocked.getError());
  Map<String,Object> config=new HashMap<>();config.put("testDevices",Collections.singletonList("TEST-DEVICE"));config.put("underAge",false);
  assertNull(call("dotnative.ads.placements","configure",config).getError());
  PluginReply status=call("dotnative.ads.placements","consentStatus",null);
  assertEquals(false,((Map<?,?>)status.getValue()).get("canRequestAds"));
  assertEquals("consent_required",call("dotnative.ads.placements","loadAppOpen",args).getError());
  assertNull(call("dotnative.ads.placements","dispose",args).getError());
 }
}
