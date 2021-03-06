﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using mobSocial.Data.Constants;
using mobSocial.Data.Entity.Settings;
using mobSocial.Data.Entity.Users;
using mobSocial.Data.Enum;
using mobSocial.Services.Extensions;
using mobSocial.Services.MediaServices;
using mobSocial.Services.Security;
using mobSocial.Services.Users;
using mobSocial.WebApi.Configuration.Infrastructure;
using mobSocial.WebApi.Configuration.Mvc;
using mobSocial.WebApi.Configuration.Security.Attributes;
using mobSocial.WebApi.Extensions.ModelExtensions;
using mobSocial.WebApi.Models.Users;

namespace mobSocial.WebApi.Controllers
{
    [RoutePrefix("users")]
    public class UserController : RootApiController
    {
        private readonly IUserService _userService;
        private readonly IRoleService _roleService;
        private readonly UserSettings _userSettings;
        private readonly SecuritySettings _securitySettings;
        private readonly MediaSettings _mediaSettings;
        private readonly IUserRegistrationService _userRegistrationService;
        private readonly ICryptographyService _cryptographyService;
        private readonly IMediaService _mediaService;

        public UserController(IUserService userService, IRoleService roleService, UserSettings userSettings, SecuritySettings securitySettings, IUserRegistrationService userRegistrationService, ICryptographyService cryptographyService, IMediaService mediaService, MediaSettings mediaSettings)
        {
            _userService = userService;
            _userSettings = userSettings;
            _securitySettings = securitySettings;
            _userRegistrationService = userRegistrationService;
            _cryptographyService = cryptographyService;
            _mediaService = mediaService;
            _mediaSettings = mediaSettings;
            _roleService = roleService;
        }

        [Route("get")]
        [AdminAuthorize]
        [HttpGet]
        public async Task<IHttpActionResult> Get([FromUri] UserGetModel requestModel)
        {
            //do we have valid values?
            if (requestModel.Page <= 0)
                requestModel.Page = 1;

            //do the needful
            var users = await _userService.GetAsync(x =>
                (!string.IsNullOrEmpty(requestModel.SearchText) &&
                (x.FirstName.StartsWith(requestModel.SearchText) ||
                x.LastName.StartsWith(requestModel.SearchText) ||
                x.Email == requestModel.SearchText)) || true, null, true, requestModel.Page, requestModel.Count);

            var model = users.ToList().Select(user => user.ToModel(_mediaService, _mediaSettings));
            return RespondSuccess(new {
                Users = model
            });
        }

        [Route("get/{id:int}")]
        [HttpGet]
        [Authorize]
        public async Task<IHttpActionResult> Get(int id)
        {
            //we need to make sure that logged in user has capability to view user
            if (!(ApplicationContext.Current.CurrentUser.IsAdministrator() || ApplicationContext.Current.CurrentUser.Id != id))
                return NotFound();

            //first get the user
            var user = await _userService.GetAsync(id);
            if (user == null)
                return NotFound();

            var model = user.ToEntityModel(_mediaService, _mediaSettings);

            //get all the available roles
            var availableRoles = _roleService.Get(x => x.IsActive).ToList();
            //we need to send the id and role name only to client 
            model.AvailableRoles = availableRoles.Select(x =>
            {
                dynamic newObject = new ExpandoObject();
                newObject.RoleName = x.RoleName;
                newObject.Id = x.Id;
                return newObject;
            }).ToList();
            return RespondSuccess(new {
                User = model
            });
        }
        [Route("get/{userName}")]
        public async Task<IHttpActionResult> Get(string userName)
        {
            var userNameUser = await _userService.GetAsync(x => x.UserName == userName, null);
            return RespondSuccess(new {
                Available = userNameUser.Any()
            });
        }

        [Route("post")]
        [HttpPost]
        [AdminAuthorize]
        public IHttpActionResult Post(UserEntityModel entityModel)
        {
            User user;
            user = entityModel.Id == 0 ? new User() : _userService.Get(entityModel.Id);

            if (user == null)
                return NotFound();

            //check if the email has already been registered
            var emailUser = _userService.Get(x => x.Email == entityModel.Email, null).FirstOrDefault();
            if (emailUser != null && emailUser.Id != user.Id)
            {
                VerboseReporter.ReportError("The email is already registered with another user", "post_user");
                return RespondFailure();
            }

            //same for user name
            if (_userSettings.AreUserNamesEnabled)
            {
                var userNameUser = _userService.Get(x => x.UserName == entityModel.UserName, null).FirstOrDefault();
                if (userNameUser != null && userNameUser.Id != user.Id)
                {
                    VerboseReporter.ReportError("The username is already taken by another user", "post_user");
                    return RespondFailure();
                }
            }

            //we should have at least one role
            if (entityModel.RoleIds.Count == 0)
            {
                VerboseReporter.ReportError("At least one role must be assigned to the user", "post_user");
                return RespondFailure();
            }
            //is this a new user, we'll require password
            if (string.IsNullOrEmpty(entityModel.Password) && entityModel.Id == 0)
            {
                VerboseReporter.ReportError("You must specify the password for the user", "post_user");
                return RespondFailure();
            }
            //are passwords same?
            if (string.Compare(entityModel.Password, entityModel.ConfirmPassword, StringComparison.Ordinal) != 0)
            {
                VerboseReporter.ReportError("The passwords do not match", "post_user");
                return RespondFailure();
            }

            user.FirstName = entityModel.FirstName;
            user.LastName = entityModel.LastName;
            user.Email = entityModel.Email;
            user.Remarks = entityModel.Remarks;
            user.Active = entityModel.Active;
            user.DateUpdated = DateTime.UtcNow;
            user.Name = string.Concat(user.FirstName, " ", user.LastName);
            user.UserName = entityModel.UserName;
            if (entityModel.Id == 0)
            {
                user.Password = entityModel.Password;
                user.LastLoginDate = null;
                _userRegistrationService.Register(user, _securitySettings.DefaultPasswordStorageFormat);
            }
            else
            {
                if (!string.IsNullOrEmpty(entityModel.Password)) // update password if provided
                {
                    if (string.IsNullOrEmpty(user.PasswordSalt))
                        user.PasswordSalt = _cryptographyService.CreateSalt(8);
                    user.Password = _cryptographyService.GetHashedPassword(entityModel.Password, user.PasswordSalt,
                        _securitySettings.DefaultPasswordStorageFormat);
                }
                    
                _userService.Update(user);
            }

            //assign the roles now
            var roles = _roleService.Get(x => x.IsActive);
            //current roles
            var currentRoleIds = user.UserRoles.Select(x => x.RoleId).ToList();
            //roles to unassign
            var rolesToUnassign = currentRoleIds.Except(entityModel.RoleIds);
            foreach (var roleId in rolesToUnassign)
            {
                var role = roles.FirstOrDefault(x => x.Id == roleId);
                if(role == null)
                    continue;

                _roleService.UnassignRoleToUser(role, user);
            }

            //roles to assign
            var rolesToAssign = entityModel.RoleIds.Except(currentRoleIds);
            foreach (var roleId in rolesToAssign)
            {
                var role = roles.FirstOrDefault(x => x.Id == roleId);
                if (role == null)
                    continue;

                _roleService.AssignRoleToUser(role, user);
            }

            //any images to assign
            if(entityModel.CoverImageId != 0)
                user.SetPropertyValue(PropertyNames.DefaultCoverId, entityModel.CoverImageId);
            if(entityModel.ProfileImageId != 0)
                user.SetPropertyValue(PropertyNames.DefaultPictureId, entityModel.ProfileImageId);

            VerboseReporter.ReportSuccess("User saved successfully", "post_user");
            return RespondSuccess(new {
                User = user.ToEntityModel(_mediaService, _mediaSettings)
            });
        }

        [Route("delete/{id:int}")]
        [AdminAuthorize]
        [HttpDelete]
        public IHttpActionResult Delete(int id)
        {
            //the users are automatically soft deletable. That means they won't be deleted actually. Instead the deleted flag is set to true
            _userService.Delete(x => x.Id == id);
            VerboseReporter.ReportSuccess("Successfully deleted the user", "delete");
            return RespondSuccess();
        }

    }
}