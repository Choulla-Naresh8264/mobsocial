﻿using System;
using System.Runtime.CompilerServices;
using System.Web;
using System.Web.Mvc;
using System.Web.Routing;
using mobSocial.Core.Infrastructure.AppEngine;
using mobSocial.Data.Database;
using mobSocial.WebApi.Configuration.Infrastructure;
using mobSocial.WebApi.Configuration.ViewEngines;

[assembly: InternalsVisibleTo("mobSocial.Tests")]
namespace mobSocial.WebApi
{
    public class MvcApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            AreaRegistration.RegisterAllAreas();

            //setup initial tasks
            mobSocialEngine.ActiveEngine.Start();

            RouteConfig.RegisterRoutes(RouteTable.Routes);

            //setup view engines
            ViewEngines.Engines.Clear();
            ViewEngines.Engines.Add(new MobSocialRazorProviderViewEngine());

        }

        protected void Application_PostAuthenticateRequest(object sender, EventArgs e)
        {
            //to avoid any kind of cookie replay attacks, we'd make sure that we have a valid user
            if (DatabaseManager.IsDatabaseInstalled() && ApplicationContext.Current.CurrentUser == null)
            {
                HttpContext.Current.User = null;
                HttpContext.Current.GetOwinContext().Authentication.User = null;
            }
        }
       
    }
}
